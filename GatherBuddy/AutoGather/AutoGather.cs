﻿using ECommons.Automation.LegacyTaskManager;
using GatherBuddy.Plugin;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.AutoGather.Movement;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Enums;
using ECommons.Throttlers;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text;
using Dalamud.Utility;
using ECommons.ExcelServices;
using ECommons;
using ECommons.Automation;
using GatherBuddy.Data;
using NodeType = GatherBuddy.Enums.NodeType;
using ECommons.UIHelpers.AddonMasterImplementations;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather : IDisposable
    {
        public AutoGather(GatherBuddy plugin)
        {
            // Initialize the task manager
            TaskManager                            =  new();
            TaskManager.ShowDebug                  =  false;
            _plugin                                =  plugin;
            _soundHelper                           =  new SoundHelper();
            _advancedUnstuck                       =  new();
        }

        private readonly GatherBuddy _plugin;
        private readonly SoundHelper _soundHelper;
        private readonly AdvancedUnstuck _advancedUnstuck;
        
        public           TaskManager TaskManager { get; }

        private bool _enabled { get; set; } = false;
        internal readonly GatheringTracker NodeTracker = new();

        public unsafe bool Enabled
        {
            get => _enabled;
            set
            {
                if (!value)
                {
                    //Do Reset Tasks
                    var gatheringMasterpiece = (AddonGatheringMasterpiece*)Dalamud.GameGui.GetAddonByName("GatheringMasterpiece", 1);
                    if (gatheringMasterpiece != null && !gatheringMasterpiece->AtkUnitBase.IsVisible)
                    {
                        gatheringMasterpiece->AtkUnitBase.IsVisible = true;
                    }

                    TaskManager.Abort();
                    targetInfo                          = null;
                    if (VNavmesh_IPCSubscriber.IsEnabled && IsPathGenerating) 
                        VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                    StopNavigation();
                    AutoStatus = "Idle...";
                    AutoGatherStatus = AutoGatherState.Idle;
                }
                else
                {
                    RefreshNextTresureMapAllowance();                    
                    WentHome = true; //Prevents going home right after enabling auto-gather
                }

                _enabled = value;
            }
        }

        public void GoHome()
        {
            StopNavigation();

            if (WentHome) return;
            WentHome = true;

            if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                return;

            if (Lifestream_IPCSubscriber.IsEnabled && !Lifestream_IPCSubscriber.IsBusy())
            {
                Lifestream_IPCSubscriber.ExecuteCommand("auto");
                TaskManager.Enqueue(() => !Lifestream_IPCSubscriber.IsBusy(), 60000);
                // Wait until Lifestream successfully reaches it's destination before marking AutoGatherStatus as Idle
                // This ensure that Artisan isnt restarted while Lifestream is Busy
                TaskManager.Enqueue(() => AutoGatherStatus = AutoGatherState.Idle);
            }
            else
            {
                GatherBuddy.Log.Warning("Lifestream not found or not ready");
                AutoGatherStatus = AutoGatherState.Idle;
            }
        }

        unsafe public void CloseRecipeAddon()
        {
            if (RecipeNoteAddon != null && RecipeNoteAddon->AtkUnitBase.IsVisible)
            {
                GatherBuddy.Log.Debug("Closing recipe menu to exit crafting state");
                TaskManager.Enqueue(() => Callback.Fire(&RecipeNoteAddon->AtkUnitBase, true, -1));
            }
        }

        private class NoGatherableItemsInNodeExceptions : Exception { }
        private class NoColectableActionsExceptions : Exception { }
        public void DoAutoGather()
        {
            if (!IsGathering)
                LuckUsed = new(0); //Reset the flag even if auto-gather was disabled mid-gathering

            if (!Enabled)
            {
                return;
            }

            try
            {
                if (!NavReady)
                {
                    AutoStatus = "Waiting for Navmesh...";
                    return;
                }
            }
            catch (Exception e)
            {
                //GatherBuddy.Log.Error(e.Message);
                AutoStatus = "vnavmesh communication failed. Do you have it installed??";
                return;
            }

            if (TaskManager.IsBusy)
            {
                //GatherBuddy.Log.Verbose("TaskManager has tasks, skipping DoAutoGather");
                return;
            }

            // Handle Character Occupied States
            if (!CanAct)
            {
                if (IsPreparingToCraft)
                {
                    if(GatherBuddy.Config.AutoGatherConfig.EnableArtisanIntegration && Artisan_IPCSubscriber.IsEnabled && !IsArtisanOperating())
                    {
                        AutoStatus = "Artisan is currently operating...";
                    }
                    else
                    {
                        CloseRecipeAddon();
                    }
                } else if (IsCrafting)
                {
                    if(GatherBuddy.Config.AutoGatherConfig.EnableArtisanIntegration && Artisan_IPCSubscriber.IsEnabled)
                    {
                        AutoStatus = "Artisan is currently operating...";
                    }
                } else
                {
                    AutoStatus = Dalamud.Conditions[ConditionFlag.Gathering] ? AutoStatus = "Gathering..." : "Player is busy...";
                    return;
                }
            }

            if (FreeInventorySlots == 0)
            {
                AbortAutoGather("Inventory is full");
                return;
            }

            if (IsGathering)
            {
                if (targetInfo != null)
                {
                    if (targetInfo.Location != null && targetInfo.Item.NodeType is NodeType.Unspoiled or NodeType.Legendary)
                        VisitedTimedLocations[targetInfo.Location] = targetInfo.Time;

                    var target = Svc.Targets.Target;
                    if (target != null
                        && target.ObjectKind is ObjectKind.GatheringPoint
                        && targetInfo.Item.NodeType is NodeType.Regular or NodeType.Ephemeral
                        && VisitedNodes.Last?.Value != target.DataId
                        && targetInfo.Location?.WorldPositions.ContainsKey(target.DataId) == true)
                    {
                        FarNodesSeenSoFar.Clear();
                        VisitedNodes.AddLast(target.DataId);
                        while (VisitedNodes.Count > (targetInfo.Location.WorldPositions.Count <= 4 ? 2 : 4))
                            VisitedNodes.RemoveFirst();
                    }
                }

                if (!GatherBuddy.Config.AutoGatherConfig.DoGathering)
                    return;

                AutoStatus = "Gathering...";
                StopNavigation();
                try
                {
                    DoActionTasks(targetInfo?.Item);
                }
                catch (NoGatherableItemsInNodeExceptions)
                {
                    CloseGatheringAddons();
                }
                catch (NoColectableActionsExceptions)
                {
                    Communicator.PrintError("Unable to pick a collectability increasing action to use. Make sure that at least one of the collectable actions is enabled.");
                    AbortAutoGather();
                }
                return;
            }

            //Cache IPC call results
            var isPathGenerating = IsPathGenerating;
            var isPathing = IsPathing;

            if (_advancedUnstuck.IsRunning || CurrentDestination != default && CurrentDestination.DistanceToPlayer() > 3 && _advancedUnstuck.Check(isPathGenerating, isPathing))
            {
                StopNavigation();
                AutoStatus = $"Advanced unstuck in progress!";
                return;
            }

            if (isPathGenerating)
            {
                AutoStatus = "Generating path...";
                lastMovementTime = DateTime.Now;
                return;
            }

            if (isPathing)
            {
                StuckCheck();
            }

            if (GatherBuddy.Config.AutoGatherConfig.DoMaterialize 
                && Player.Job is Job.BTN or Job.MIN
                && !isPathing 
                && !Svc.Condition[ConditionFlag.Mounted] 
                && SpiritBondMax > 0)
            {
                DoMateriaExtraction();
                return;
            }

            if (GatherBuddy.Config.AutoGatherConfig.EnableArtisanIntegration && Artisan_IPCSubscriber.IsEnabled)
            {
                if (AutoGatherStatus == AutoGatherState.Idle && !GenericHelpers.IsOccupied() && WasArtisanPaused)
                {
                    RestartArtisan();
                    return;
                }
            }

            foreach (var (loc, time) in VisitedTimedLocations)
                if (time.End < AdjuctedServerTime)
                    VisitedTimedLocations.Remove(loc);

            {//Block to limit the scope of the variable "next"
                UpdateItemsToGather();
                var next = ItemsToGather.FirstOrDefault();

                if (next == null)
                {
                    if (!_plugin.GatherWindowManager.ActiveItems.OfType<Gatherable>().Any(i => InventoryCount(i) < QuantityTotal(i) && !(i.IsTreasureMap && InventoryCount(i) != 0)))
                    {
                        AbortAutoGather();
                        return;
                    }

                    if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle)
                        GoHome();

                    if (GatherBuddy.Config.AutoGatherConfig.EnableArtisanIntegration && Artisan_IPCSubscriber.IsEnabled && IsArtisanOperating())
                        AutoStatus = "Artisan is currently operating...";
                    else
                        AutoStatus = "No available items to gather";
                    
                    return;
                }
                AutoGatherStatus = AutoGatherState.Gathering;

                if (GatherBuddy.Config.AutoGatherConfig.EnableArtisanIntegration && Artisan_IPCSubscriber.IsEnabled) {
                    if(IsArtisanOperating() && !WasArtisanPaused)
                    {
                        PauseArtisan();
                        return;
                    }
                }

                if (targetInfo == null
                    || targetInfo.Location == null
                    || targetInfo.Time.End < AdjuctedServerTime
                    || targetInfo.Item != next.Item
                    || VisitedTimedLocations.ContainsKey(targetInfo.Location))
                {
                    //Find a new location only if the target item changes or the node expires to prevent switching to another node when a new one goes up
                    targetInfo = next;
                    FarNodesSeenSoFar.Clear();
                    VisitedNodes.Clear();
                }
            }

            if (targetInfo.Location == null)
            {
                //Should not happen because UpdateItemsToGather filters out all unavailable items
                GatherBuddy.Log.Debug("Couldn't find any location for the target item");
                return;
            }

            if (targetInfo.Item.IsTreasureMap && NextTresureMapAllowance == DateTime.MinValue)
            {
                //Wait for timer refresh
                RefreshNextTresureMapAllowance();
                return;
            }

            if (!LocationMatchesJob(targetInfo.Location))
            {
                if (!ChangeGearSet(targetInfo.Location.GatheringType.ToGroup()))
                    AbortAutoGather();

                return;
            }

            //This check must be done after changing jobs.
            if (targetInfo.Item.ItemData.IsCollectable && !CheckCollectablesUnlocked())
            {
                AbortAutoGather();
                return;
            }

            var territoryId = Svc.ClientState.TerritoryType;
            //Idyllshire to The Dravanian Hinterlands
            if (territoryId == 478 && targetInfo.Location.Territory.Id == 399 && Lifestream_IPCSubscriber.IsEnabled)
            {
                var aetheryte = Svc.Objects.Where(x => x.ObjectKind == ObjectKind.Aetheryte && x.IsTargetable).OrderBy(x => x.Position.DistanceToPlayer()).FirstOrDefault();
                if (aetheryte != null)
                {
                    if (aetheryte.Position.DistanceToPlayer() > 10)
                    {
                        AutoStatus = "Moving to aetheryte...";
                        if (!isPathing && !isPathGenerating) Navigate(aetheryte.Position, false);
                    }
                    else if (!Lifestream_IPCSubscriber.IsBusy())
                    {
                        AutoStatus = "Teleporting...";
                        StopNavigation();
                        var exit = targetInfo.Location.DefaultXCoord < 2000 ? 91u : 92u;
                        var name = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRow(exit).AethernetName.Value.Name.ToString();
                        Lifestream_IPCSubscriber.AethernetTeleport(name);
                    }
                    return;
                }
            }

            var forcedAetheryte = ForcedAetherytes.ZonesWithoutAetherytes.Where(z => z.ZoneId == targetInfo.Location.Territory.Id).FirstOrDefault();
            if (forcedAetheryte.ZoneId != 0 
                && (GatherBuddy.GameData.Aetherytes[forcedAetheryte.AetheryteId].Territory.Id == territoryId
                || forcedAetheryte.AetheryteId == 70 && territoryId == 886)) //The Firmament
            {
                if (territoryId == 478 && !Lifestream_IPCSubscriber.IsEnabled)
                    AutoStatus = $"Install Lifestraem or teleport to {targetInfo.Location.Territory.Name} manually";
                else
                    AutoStatus = "Manual teleporting required";
                return;
            }

            //At this point, we are definitely going to gather something, so we may go home after that.
            if (Lifestream_IPCSubscriber.IsEnabled) Lifestream_IPCSubscriber.Abort();
            WentHome = false;

            if (targetInfo.Location.Territory.Id != territoryId)
            {
                if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
                {
                    AutoStatus = "Can not teleport when bound by duty";
                    return;
                }
                AutoStatus = "Teleporting...";
                StopNavigation();

                if (!MoveToTerritory(targetInfo.Location))
                    AbortAutoGather();

                return;
            }

            if (ActivateGatheringBuffs(targetInfo.Item.NodeType is NodeType.Unspoiled or NodeType.Legendary))
                return;

            if (DoUseConsumablesWithoutCastTime())
                return;

            var allPositions = targetInfo.Location.WorldPositions
                .ExceptBy(VisitedNodes, n => n.Key)
                .SelectMany(w => w.Value)
                .Where(v => !IsBlacklisted(v))
                .ToHashSet();

            var visibleNodes = Svc.Objects
                .Where(o => allPositions.Contains(o.Position))
                .ToList();

            var closestTargetableNode = visibleNodes
                .Where(o => o.IsTargetable)
                .MinBy(o => Vector3.Distance(Player.Position, o.Position));

            if (closestTargetableNode != null)
            {
                AutoStatus = "Moving to node...";
                MoveToCloseNode(closestTargetableNode, targetInfo.Item);
                return;
            }

            AutoStatus = "Moving to far node...";

            if (CurrentDestination != default && CurrentFarNodeLocation != targetInfo.Location)
            {
                GatherBuddy.Log.Debug("Current destination doesn't match the target location, resetting navigation");
                StopNavigation();
                FarNodesSeenSoFar.Clear();
                VisitedNodes.Clear();
            }

            CurrentFarNodeLocation = targetInfo.Location;

            if (CurrentDestination != default)
            {
                var currentNode = visibleNodes.FirstOrDefault(o => o.Position == CurrentDestination);
                if (currentNode != null && !currentNode.IsTargetable)
                    GatherBuddy.Log.Verbose($"Far node is not targetable, distance {currentNode.Position.DistanceToPlayer()}.");

                //It takes some time (roundtrip to the server) before a node becomes targetable after it becomes visible,
                //so we need to delay excluding it. But instead of measuring time, we use distance, since character is traveling at a constant speed.
                //Value 80 was determined empirically.
                foreach (var node in visibleNodes.Where(o => o.Position.DistanceToPlayer() < 80))
                    FarNodesSeenSoFar.Add(node.Position);

                if (CurrentDestination.DistanceToPlayer() < 80)
                {
                    GatherBuddy.Log.Verbose("Far node is not targetable, choosing another");
                }
                else
                {
                    return;
                }
            }

            Vector3 selectedFarNode;
            
            // only Legendary and Unspoiled show marker
            if (ShouldUseFlag && targetInfo.Item.NodeType is NodeType.Legendary or NodeType.Unspoiled)
            {
                var pos = TimedNodePosition;
                // marker not yet loaded on game
                if (pos == null || targetInfo.Time.Start > GatherBuddy.Time.ServerTime.AddSeconds(5))
                {
                    AutoStatus = "Waiting on flag show up";
                    return;
                }

                selectedFarNode = allPositions
                    .Where(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)) < 10)
                    .OrderBy(o => Vector2.Distance(pos.Value, new Vector2(o.X, o.Z)))
                    .FirstOrDefault();
                if (selectedFarNode == default)
                    selectedFarNode = VNavmesh_IPCSubscriber.Query_Mesh_NearestPoint(new Vector3(pos.Value.X, 0, pos.Value.Y), 10, 10000);
            }
            else
            {
                //Select the closest node
                selectedFarNode = allPositions
                    .OrderBy(v => Vector3.Distance(Player.Position, v))
                    .FirstOrDefault(n => !FarNodesSeenSoFar.Contains(n));

                if (selectedFarNode == default)
                {
                    FarNodesSeenSoFar.Clear();
                    GatherBuddy.Log.Verbose($"Selected node was null and far node filters have been cleared");
                    return;
                }

            }
             
            MoveToFarNode(selectedFarNode);
        }

        private void AbortAutoGather(string? status = null)
        {
            Enabled = false;
            if (!string.IsNullOrEmpty(status))
                AutoStatus = status;
            if (GatherBuddy.Config.AutoGatherConfig.HonkMode)
                _soundHelper.PlayHonkSound(3);
            CloseGatheringAddons();
            if (GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone)
                EnqueueActionWithDelay(GoHome);
        }

        private unsafe void CloseGatheringAddons()
        {
            var masterpieceOpen = MasterpieceAddon != null;
            var gatheringOpen = GatheringAddon != null;
            if (masterpieceOpen)
            {
                EnqueueActionWithDelay(() => { if (MasterpieceAddon is var addon and not null) { Callback.Fire(&addon->AtkUnitBase, true, -1); } });
                TaskManager.Enqueue(() => MasterpieceAddon == null);
                TaskManager.Enqueue(() => GatheringAddon != null);
            }
            if (gatheringOpen || masterpieceOpen)
            {
                EnqueueActionWithDelay(() => { if (GatheringAddon is var addon and not null) { Callback.Fire(&addon->AtkUnitBase, true, -1); } });
                TaskManager.Enqueue(() => {
                    var addon = (AddonSelectYesno*)Dalamud.GameGui.GetAddonByName("SelectYesno");
                    if (addon != null)
                    {
                        EnqueueActionWithDelay(() =>
                        {
                            if ((AddonSelectYesno*)Dalamud.GameGui.GetAddonByName("SelectYesno") is var addon and not null)
                            {
                                var master = new AddonMaster.SelectYesno(addon);
                                master.Yes();
                            }
                        });
                        TaskManager.Enqueue(() => !IsGathering);
                        return true;
                    }

                    return IsGathering;       
                });
            }
        }

        private static unsafe void RefreshNextTresureMapAllowance()
        {
            if (EzThrottler.Throttle("RequestResetTimestamps", 1000))
            {
                FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance()->RequestResetTimestamps();
            }
        }

        private bool CheckCollectablesUnlocked()
        {
            if (Player.Level < Actions.Collect.MinLevel)
            {
                Communicator.PrintError("You've put a collectable on the gathering list, but your level is not high enough to gather it.");
                return false;
            }
            if (Actions.Collect.QuestID != 0 && !QuestManager.IsQuestComplete(Actions.Collect.QuestID))
            {
                Communicator.PrintError("You've put a collectable on the gathering list, but you haven't unlocked the collectables.");
                var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Quest>()!;
                var row = sheet.GetRow(Actions.Collect.QuestID)!;
                var loc = row.IssuerLocation.Value!;
                var map = loc.Map.Value!;
                var pos = MapUtil.WorldToMap(new Vector2(loc.X, loc.Z), map);
                var mapPayload = new MapLinkPayload(loc.Territory.RowId, loc.Map.RowId, pos.X, pos.Y);
                var text = new SeStringBuilder();
                text.AddText("Collectables are unlocked by ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .AddQuestLink(Actions.Collect.QuestID)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(row.Name.ToString())
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(" quest, which can be started in ")
                    .AddUiForeground(0x0225)
                    .AddUiGlow(0x0226)
                    .Add(mapPayload)
                    .AddUiForeground(500)
                    .AddUiGlow(501)
                    .AddText($"{(char)SeIconChar.LinkMarker}")
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText($"{mapPayload.PlaceName} {mapPayload.CoordinateString}")
                    .Add(RawPayload.LinkTerminator)
                    .AddUiGlowOff()
                    .AddUiForegroundOff()
                    .AddText(".");
                Communicator.Print(text.BuiltString);
                return false;
            }
            return true;
        }

        private bool ChangeGearSet(GatheringType job)
        {
            var set = job switch
            {
                GatheringType.Miner => GatherBuddy.Config.MinerSetName,
                GatheringType.Botanist => GatherBuddy.Config.BotanistSetName,
                _ => null,
            };
            if (string.IsNullOrEmpty(set))
            {
                Communicator.PrintError($"No gear set for {job} configured.");
                return false;
            }

            Chat.Instance.ExecuteCommand($"/gearset change \"{set}\"");
            TaskManager.DelayNext(Random.Shared.Next(500, 1500));  //Add a random delay to be less suspicious 
            return true;
        }

        public void Dispose()
        {
            _advancedUnstuck.Dispose();
            NodeTracker.Dispose();
        }
    }
}
