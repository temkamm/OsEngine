/*
 * Your rights to use code governed by this license http://o-s-a.net/doc/license_simple_engine.pdf
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.OsTrader.Iceberg;
using System;
using System.IO;

namespace OsEngine.OsTrader.Panels.Tab.SyntheticBondTab
{
    public enum BondScenarioState
    {
        Stopped,
        Running,
        PauseOnlyClose,
        StoppingNow
    }

    public class BondScenario
    {
        #region Constructor

        public string UniqueName;

        public string ScriptName;

        public int ScenarioNumber;

        public bool IsActiveScenario = false;

        private StartProgram StartProgram;

        public BondScenario(string nameScript, string uniqueName, int scenarioNumber, StartProgram startProgram)
        {
            UniqueName = uniqueName;
            ScriptName = nameScript;
            ScenarioNumber = scenarioNumber;
            StartProgram = startProgram;

            LoadBondScenario();

            if (NonTradePeriods == null)
            {
                NonTradePeriods = new NonTradePeriods(UniqueName);
            }

            if (ArbitrationIceberg == null)
            {
                ArbitrationIceberg = new ArbitrationIceberg(UniqueName + "ArbitrationIceberg", StartProgram);
                ArbitrationIceberg.NonTradePeriods = NonTradePeriods;
            }

            ArbitrationIceberg.AllPositionsFilledEvent += OnAllPositionsFilled;
            ArbitrationIceberg.AllPositionsClosedEvent += OnAllPositionsClosed;

            RestoreSafeStateAfterLoad();
        }

        private void LoadBondScenario()
        {
            if (!File.Exists(@"Engine\" + UniqueName + @"ToLoad.txt"))
            {
                return;
            }

            using (StreamReader reader = new StreamReader(@"Engine\" + UniqueName + @"ToLoad.txt"))
            {
                MaxSpread = reader.ReadLine().ToDecimal();
                MinSpread = reader.ReadLine().ToDecimal();
                IsActiveScenario = Convert.ToBoolean(reader.ReadLine());
                ScriptName = reader.ReadLine();

                ArbitrationIceberg = new ArbitrationIceberg(reader.ReadLine(), StartProgram);
                NonTradePeriods = new NonTradePeriods(reader.ReadLine());

                string stateLine = reader.ReadLine();
                if (!string.IsNullOrEmpty(stateLine)
                    && Enum.TryParse(stateLine, out BondScenarioState loadedState))
                {
                    State = loadedState;
                }

                string cyclesCountLine = reader.ReadLine();
                if (!string.IsNullOrEmpty(cyclesCountLine)
                    && int.TryParse(cyclesCountLine, out int cyclesCount))
                {
                    CyclesCount = cyclesCount;
                }

                string completedCyclesLine = reader.ReadLine();
                if (!string.IsNullOrEmpty(completedCyclesLine)
                    && int.TryParse(completedCyclesLine, out int completedCycles))
                {
                    CompletedCycles = completedCycles;
                }

                string maxQuoteAgeSecondsLine = reader.ReadLine();
                if (!string.IsNullOrEmpty(maxQuoteAgeSecondsLine)
                    && int.TryParse(maxQuoteAgeSecondsLine, out int maxQuoteAgeSeconds)
                    && maxQuoteAgeSeconds >= 0)
                {
                    MaxQuoteAgeSeconds = maxQuoteAgeSeconds;
                }
            }
        }

        public void Save()
        {
            using (StreamWriter writer = new StreamWriter(@"Engine\" + UniqueName + @"ToLoad.txt", false))
            {
                writer.WriteLine(MaxSpread.ToString());
                writer.WriteLine(MinSpread.ToString());
                writer.WriteLine(IsActiveScenario.ToString());
                writer.WriteLine(ScriptName.ToString());
                writer.WriteLine(ArbitrationIceberg.UniqueName.ToString());
                writer.WriteLine(NonTradePeriods.NameUnique.ToString());
                writer.WriteLine(State.ToString());
                writer.WriteLine(CyclesCount.ToString());
                writer.WriteLine(CompletedCycles.ToString());
                writer.WriteLine(MaxQuoteAgeSeconds.ToString());

                ArbitrationIceberg.Save();
                NonTradePeriods.Save();
            }
        }

        /// <summary>
        /// Deletes this script
        /// | Удаляет данный сценарий
        /// </summary>
        public void Delete()
        {
            ArbitrationIceberg?.Delete();
            NonTradePeriods?.Delete();

            if (File.Exists(@"Engine\" + UniqueName + @"ToLoad.txt"))
            {
                File.Delete(@"Engine\" + UniqueName + @"ToLoad.txt");
            }
        }

        public void Clear()
        {
            try
            {
                ArbitrationIceberg.Clear();
            }
            catch
            {
                // ignore
            }
        }

        public bool IsReadyToTrade()
        {
            try
            {
                if (ArbitrationIceberg == null)
                    return false;

                return ArbitrationIceberg.CheckTradingReady();
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
                return false;
            }
        }

        private void RestoreSafeStateAfterLoad()
        {
            BondScenarioState stateBefore = State;
            bool hasPosition = HasActivePosition();

            if (State == BondScenarioState.PauseOnlyClose && !hasPosition)
            {
                State = BondScenarioState.Stopped;
            }
            else if (State == BondScenarioState.StoppingNow)
            {
                State = hasPosition
                    ? BondScenarioState.PauseOnlyClose
                    : BondScenarioState.Stopped;
            }

            if (stateBefore != State)
            {
                ServerMaster.SendNewLogMessage(
                    "Scenario " + ScriptName + ": restored state after restart. "
                    + stateBefore + " -> " + State,
                    LogMessageType.System);

                Save();
            }
        }

        public bool HasActivePosition()
        {
            if (ArbitrationIceberg == null)
            {
                return false;
            }

            if (HasActivePositionInLegs(ArbitrationIceberg.MainLegs))
            {
                return true;
            }

            return HasActivePositionInLegs(ArbitrationIceberg.SecondaryLegs);
        }

        public void SetCurrentBidAskSpread(decimal spread)
        {
            lock (_runtimeDataLocker)
            {
                _currentBidAskSpread = spread;
                _hasCurrentBidAskSpread = true;
            }
        }

        public bool TryGetCurrentBidAskSpread(out decimal spread)
        {
            lock (_runtimeDataLocker)
            {
                spread = _currentBidAskSpread;
                return _hasCurrentBidAskSpread;
            }
        }

        private bool HasActivePositionInLegs(System.Collections.Generic.List<ArbitrationLeg> legs)
        {
            if (legs == null)
            {
                return false;
            }

            for (int i = 0; i < legs.Count; i++)
            {
                ArbitrationLeg leg = legs[i];

                if (leg == null
                    || leg.ArbitrationLegStatistic == null
                    || leg.ArbitrationLegStatistic.CurrentPosition == null)
                {
                    continue;
                }

                Position position = leg.ArbitrationLegStatistic.CurrentPosition;

                if (position.State != PositionStateType.Done
                    && position.State != PositionStateType.OpeningFail
                    && position.OpenVolume > 0)
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Public fields

        /// <summary>
        /// The trading module for this scenario. | Торговый модуль данного сценария.
        /// </summary>
        public ArbitrationIceberg ArbitrationIceberg;

        /// <summary>
        /// The maximum spread at which the bond opens. | Максимальный спред, при котором облигация открывается.
        /// </summary>
        public decimal MaxSpread;

        /// <summary>
        /// The minimum spread at which the position closes. | Минимальный спред, при котором позиция закрывается.
        /// </summary>
        public decimal MinSpread;

        public BondScenarioState State = BondScenarioState.Stopped;

        public int CyclesCount;

        public int CompletedCycles;

        public int MaxQuoteAgeSeconds = 10;

        /// <summary>
        /// Non-trading periods for this scenario. | Неторговые периоды данного сценария.
        /// </summary>
        public NonTradePeriods NonTradePeriods;

        private readonly object _runtimeDataLocker = new object();

        private decimal _currentBidAskSpread;

        private bool _hasCurrentBidAskSpread;

        #endregion

        #region Events

        /// <summary>
        /// Fires when all legs have been filled to target volume.
        /// | Вызывается, когда все ноги набрали целевой объём.
        /// </summary>
        public Action<string> ScenarioFilledEvent;

        /// <summary>
        /// Fires when all legs have been closed.
        /// | Вызывается, когда все ноги закрыты.
        /// </summary>
        public Action<string> ScenarioClosedEvent;

        private void OnAllPositionsFilled()
        {
            ScenarioFilledEvent?.Invoke(UniqueName);
        }

        private void OnAllPositionsClosed()
        {
            ScenarioClosedEvent?.Invoke(UniqueName);
        }

        #endregion
    }
}
