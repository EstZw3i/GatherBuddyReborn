using ECommons.ExcelServices;
using ECommons.GameHelpers;
using Pair = (uint Botanist, uint Miner);

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    public static class Actions
    {
        public enum EffectType
        {
            Other,
            Yield,
            BoonYield,
            CrystalsYield,
            GatherChance,
            BoonChance,
            Integrity
        }
        public readonly struct BaseAction
        {
            public BaseAction(uint btnActionId, uint minActionId, uint btnEffectId = 0, uint minEffectId = 0, EffectType type = EffectType.Other)
            {
                action = (btnActionId, minActionId);
                effect = (btnEffectId, minEffectId == 0 ? btnEffectId : minEffectId);
                EffectType = type;

                var actionsSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                var botanistRow = actionsSheet.GetRow(action.Botanist)!;
                var minerRow = actionsSheet.GetRow(action.Miner)!;

                quest.Botanist = botanistRow.UnlockLink.RowId;
                quest.Miner = minerRow.UnlockLink.RowId;
                MinLevel = botanistRow.ClassJobLevel;
                GpCost = botanistRow.PrimaryCostValue;
            }
            private readonly Pair action;
            private readonly Pair quest;
            private readonly Pair effect;


            public uint ActionID => GetJobValue(action);
            public uint QuestID  => GetJobValue(quest);
            public uint EffectId => GetJobValue(effect);
            public int MinLevel { get; }
            public int GpCost { get; }
            public EffectType EffectType { get; }

            private static uint GetJobValue(Pair pair)
            {
                return Player.Job switch
                {
                    Job.BTN => pair.Botanist,
                    Job.MIN => pair.Miner,
                    _ => 0
                };
            }
        }

        public static readonly BaseAction Prospect      = new(  210,   227,   217, 225);
        public static readonly BaseAction Sneak         = new(  304,   303,    47);
        public static readonly BaseAction TwelvesBounty = new(  282,   280,   825, type: EffectType.CrystalsYield);
        public static readonly BaseAction Bountiful     = new( 4087,  4073,   756, type: EffectType.Yield);
        public static readonly BaseAction SolidAge      = new(  215,   232,  2765, type: EffectType.Integrity);
        public static readonly BaseAction Yield1        = new(  222,   239,   219, type: EffectType.Yield);
        public static readonly BaseAction Yield2        = new(  224,   241,   219, type: EffectType.Yield);
        public static readonly BaseAction Truth         = new(  221,   238,   221, 222);
        public static readonly BaseAction Collect       = new(  815,   240);
        public static readonly BaseAction Scour         = new(22186, 22182);
        public static readonly BaseAction Brazen        = new(22187, 22183);
        public static readonly BaseAction Meticulous    = new(22188, 22184);
        public static readonly BaseAction Scrutiny      = new(22189, 22185,    757);
        public static readonly BaseAction Luck          = new( 4095,  4081);
        public static readonly BaseAction BountifulII   = new(  273,   272,   1286, type: EffectType.Yield);
        public static readonly BaseAction GivingLand    = new( 4590,  4589,   1802, type: EffectType.CrystalsYield);
        public static readonly BaseAction Wise          = new(26522, 26521,         type: EffectType.Integrity);

    }
}
