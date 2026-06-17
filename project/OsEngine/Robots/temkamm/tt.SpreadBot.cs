/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.OsTrader.Iceberg;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.OsTrader.Panels.Tab.SyntheticBondTab;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OsEngine.Robots.SyntheticBond
{
    [Bot("ttSpreadBot")]
    public class ttSpreadBot : BotPanel
    {
        private const decimal RecheckSpreadBufferPercent = 0.01m;

        private readonly BotTabSyntheticBond _tab;
        private readonly HashSet<string> _closingScenarioNames = new HashSet<string>();
        private readonly Dictionary<string, ArbitrationMode> _scenarioTradeModes = new Dictionary<string, ArbitrationMode>();
        private readonly Dictionary<string, string> _scenarioLastBlockedReason = new Dictionary<string, string>();
        private readonly Dictionary<string, ScenarioQuoteState> _scenarioQuotes = new Dictionary<string, ScenarioQuoteState>();
        private readonly Dictionary<string, LegSubscription> _legSubscriptions = new Dictionary<string, LegSubscription>();
        private readonly object _quoteLocker = new object();
        private readonly object _processLocker = new object();
        private readonly Thread _subscriptionThread;

        private volatile bool _isDeleted;

        public ttSpreadBot(string name, StartProgram startProgram)
            : base(name, startProgram)
        {
            TabCreate(BotTabType.SyntheticBond);
            _tab = TabsSyntheticBond[0];
            _tab.SeparationChangeEvent += OnSeparationChangeEvent;

            Description = "SyntheticBond spread trader. Uses real bid/ask spread from both legs and selected scenario mode.";

            _subscriptionThread = new Thread(ScenarioSubscriptionThread)
            {
                IsBackground = true,
                Name = "ttSpreadBot quote subscription"
            };
            _subscriptionThread.Start();

            DeleteEvent += OnDeleteEvent;
        }

        public override string GetNameStrategyType()
        {
            return "ttSpreadBot";
        }

        public override void ShowIndividualSettingsDialog()
        {
        }

        private void OnDeleteEvent()
        {
            _isDeleted = true;

            if (_tab != null)
            {
                _tab.SeparationChangeEvent -= OnSeparationChangeEvent;
            }

            UnsubscribeAllQuoteEvents();
        }

        private void ScenarioSubscriptionThread()
        {
            while (!_isDeleted)
            {
                try
                {
                    EnsureAllScenarioSubscriptions();
                }
                catch (Exception ex)
                {
                    SendNewLogMessage(ex.ToString(), LogMessageType.Error);
                }

                Thread.Sleep(1000);
            }
        }

        private void OnSeparationChangeEvent(Entity.SyntheticBondEntity.SyntheticBond syntheticBond)
        {
            try
            {
                EnsureSyntheticBondSubscriptions(syntheticBond);
            }
            catch (Exception ex)
            {
                SendNewLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        private void EnsureAllScenarioSubscriptions()
        {
            if (_tab == null
                || _tab.SyntheticBondSeries == null)
            {
                return;
            }

            for (int i = 0; i < _tab.SyntheticBondSeries.Count; i++)
            {
                SyntheticBondSeries series = _tab.SyntheticBondSeries[i];

                if (series == null
                    || series.SyntheticBonds == null)
                {
                    continue;
                }

                for (int j = 0; j < series.SyntheticBonds.Count; j++)
                {
                    EnsureSyntheticBondSubscriptions(series.SyntheticBonds[j]);
                }
            }
        }

        private void EnsureSyntheticBondSubscriptions(Entity.SyntheticBondEntity.SyntheticBond syntheticBond)
        {
            if (syntheticBond == null
                || syntheticBond.ActiveScenarios == null)
            {
                return;
            }

            for (int i = 0; i < syntheticBond.ActiveScenarios.Count; i++)
            {
                EnsureScenarioSubscriptions(syntheticBond.ActiveScenarios[i]);
            }
        }

        private void EnsureScenarioSubscriptions(BondScenario scenario)
        {
            if (scenario == null
                || scenario.ArbitrationIceberg == null
                || scenario.ArbitrationIceberg.MainLegs == null
                || scenario.ArbitrationIceberg.MainLegs.Count == 0
                || scenario.ArbitrationIceberg.SecondaryLegs == null
                || scenario.ArbitrationIceberg.SecondaryLegs.Count == 0)
            {
                return;
            }

            lock (_quoteLocker)
            {
                ScenarioQuoteState quoteState = GetScenarioQuoteStateLocked(scenario.UniqueName);
                quoteState.Scenario = scenario;
            }

            EnsureLegSubscription(scenario, scenario.ArbitrationIceberg.MainLegs[0], isBaseLeg: true);
            EnsureLegSubscription(scenario, scenario.ArbitrationIceberg.SecondaryLegs[0], isBaseLeg: false);
        }

        private void EnsureLegSubscription(BondScenario scenario, ArbitrationLeg leg, bool isBaseLeg)
        {
            if (leg == null
                || leg.BotTab == null)
            {
                return;
            }

            string key = GetLegSubscriptionKey(scenario.UniqueName, isBaseLeg);

            lock (_quoteLocker)
            {
                if (_legSubscriptions.TryGetValue(key, out LegSubscription existing)
                    && existing.Tab == leg.BotTab)
                {
                    return;
                }

                if (existing != null)
                {
                    existing.Unsubscribe();
                    _legSubscriptions.Remove(key);
                }

                LegSubscription subscription = new LegSubscription(
                    leg.BotTab,
                    md => OnMarketDepthUpdate(scenario.UniqueName, isBaseLeg, md),
                    (bid, ask) => OnBestBidAskUpdate(scenario.UniqueName, isBaseLeg, bid, ask));

                subscription.Subscribe();
                _legSubscriptions[key] = subscription;
            }
        }

        private void UnsubscribeAllQuoteEvents()
        {
            lock (_quoteLocker)
            {
                foreach (LegSubscription subscription in _legSubscriptions.Values)
                {
                    subscription.Unsubscribe();
                }

                _legSubscriptions.Clear();
            }
        }

        private void OnMarketDepthUpdate(string scenarioName, bool isBaseLeg, MarketDepth marketDepth)
        {
            try
            {
                if (marketDepth == null)
                {
                    return;
                }

                bool hasBid = marketDepth.Bids != null
                    && marketDepth.Bids.Count > 0
                    && marketDepth.Bids[0].Price > 0;

                bool hasAsk = marketDepth.Asks != null
                    && marketDepth.Asks.Count > 0
                    && marketDepth.Asks[0].Price > 0;

                if (!hasBid && !hasAsk)
                {
                    return;
                }

                decimal bidPrice = hasBid ? (decimal)marketDepth.Bids[0].Price : 0;
                decimal bidVolume = hasBid ? (decimal)marketDepth.Bids[0].Bid : 0;
                decimal askPrice = hasAsk ? (decimal)marketDepth.Asks[0].Price : 0;
                decimal askVolume = hasAsk ? (decimal)marketDepth.Asks[0].Ask : 0;

                bool changed;

                lock (_quoteLocker)
                {
                    LegQuote quote = GetLegQuoteLocked(scenarioName, isBaseLeg);
                    changed = quote.Update(hasBid, bidPrice, bidVolume, hasAsk, askPrice, askVolume);
                }

                if (changed)
                {
                    ProcessScenarioByName(scenarioName);
                }
            }
            catch (Exception ex)
            {
                SendNewLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        private void OnBestBidAskUpdate(string scenarioName, bool isBaseLeg, decimal bid, decimal ask)
        {
            try
            {
                bool changed;

                lock (_quoteLocker)
                {
                    LegQuote quote = GetLegQuoteLocked(scenarioName, isBaseLeg);
                    changed = quote.UpdatePrices(bid, ask);
                }

                if (changed)
                {
                    ProcessScenarioByName(scenarioName);
                }
            }
            catch (Exception ex)
            {
                SendNewLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        private void ProcessScenarioByName(string scenarioName)
        {
            BondScenario scenario = null;

            lock (_quoteLocker)
            {
                if (_scenarioQuotes.TryGetValue(scenarioName, out ScenarioQuoteState state))
                {
                    scenario = state.Scenario;
                }
            }

            if (scenario == null)
            {
                return;
            }

            lock (_processLocker)
            {
                ProcessScenario(scenario);
            }
        }

        private void ProcessScenario(BondScenario scenario)
        {
            ArbitrationIceberg iceberg = scenario.ArbitrationIceberg;

            if (!IsScenarioConnected(iceberg))
            {
                if (scenario.State != BondScenarioState.Stopped)
                {
                    LogBlockedReason(scenario, "scenario is not connected to both trading legs");
                }

                return;
            }

            bool hasTradeMode = TryGetScenarioTradeMode(scenario, out ArbitrationMode tradeMode);
            bool hasDisplaySpread = false;
            decimal displaySpread = 0;
            string displayReason = null;

            if (hasTradeMode)
            {
                hasDisplaySpread = TryCalculateDisplaySpread(scenario, tradeMode, out displaySpread, out displayReason);

                if (hasDisplaySpread)
                {
                    scenario.SetCurrentBidAskSpread(displaySpread);
                }
            }

            if (scenario.State == BondScenarioState.Stopped)
            {
                return;
            }

            bool hasPosition = HasActivePosition(iceberg);

            if (TryCompletePendingClose(scenario, hasPosition))
            {
                return;
            }

            if ((scenario.State == BondScenarioState.PauseOnlyClose
                || scenario.State == BondScenarioState.StoppingNow)
                && !hasPosition)
            {
                SetScenarioState(
                    scenario,
                    BondScenarioState.Stopped,
                    $"Scenario {scenario.ScriptName}: position is closed. State switched to Stopped.");
                return;
            }

            if (scenario.State == BondScenarioState.StoppingNow)
            {
                return;
            }

            if (scenario.MaxSpread <= scenario.MinSpread)
            {
                LogBlockedReason(
                    scenario,
                    $"invalid spread settings. MaxSpread={scenario.MaxSpread} must be greater than MinSpread={scenario.MinSpread}");
                return;
            }

            if (scenario.CyclesCount <= 0)
            {
                SetScenarioState(
                    scenario,
                    BondScenarioState.Stopped,
                    $"Scenario {scenario.ScriptName}: invalid cycles count {scenario.CyclesCount}. State switched to Stopped.");
                return;
            }

            if (!iceberg.CheckTradingReady())
            {
                LogBlockedReason(scenario, "iceberg is not ready to trade");
                return;
            }

            if (!hasTradeMode)
            {
                LogBlockedReason(scenario, "trade mode is not defined");
                return;
            }

            if (!hasDisplaySpread)
            {
                LogBlockedReason(scenario, displayReason);
                return;
            }

            if (hasPosition)
            {
                if (IsOpenExecutionInProgress(iceberg))
                {
                    LogBlockedReason(scenario, "entry execution is still in progress");
                    return;
                }

                ProcessExitScenario(scenario, tradeMode, displaySpread);
                return;
            }

            ProcessEntryScenario(scenario, tradeMode, displaySpread);
        }

        private void ProcessEntryScenario(BondScenario scenario, ArbitrationMode tradeMode, decimal displaySpread)
        {
            if (scenario.State != BondScenarioState.Running)
            {
                return;
            }

            if (IsCyclesLimitReached(scenario))
            {
                SetScenarioState(
                    scenario,
                    BondScenarioState.Stopped,
                    $"Scenario {scenario.ScriptName}: cycles limit reached. Completed={scenario.CompletedCycles}, limit={scenario.CyclesCount}. State switched to Stopped.");
                return;
            }

            ArbitrationIceberg iceberg = scenario.ArbitrationIceberg;

            if (iceberg.CurrentStatus == ArbitrationStatus.On)
            {
                LogBlockedReason(scenario, $"iceberg is already active in mode {iceberg.CurrentMode}");
                return;
            }

            if (!TryCalculateEntrySpread(scenario, tradeMode, out decimal entrySpread, out string reason))
            {
                LogBlockedReason(scenario, reason);
                return;
            }

            if (entrySpread < scenario.MaxSpread)
            {
                return;
            }

            ResetBlockedReason(scenario);

            SendNewLogMessage(
                $"Scenario {scenario.ScriptName}: entry signal. EntrySpread={entrySpread}, DisplaySpread={displaySpread}, mode={tradeMode}, MaxSpread={scenario.MaxSpread}",
                LogMessageType.System);

            if (!TryCalculateEntrySpread(scenario, tradeMode, out decimal recheckSpread, out reason)
                || recheckSpread < scenario.MaxSpread + RecheckSpreadBufferPercent)
            {
                SendNewLogMessage(
                    $"Scenario {scenario.ScriptName}: entry cancelled before orders. RecheckSpread={recheckSpread}, required={scenario.MaxSpread + RecheckSpreadBufferPercent}",
                    LogMessageType.System);
                return;
            }

            iceberg.Start(tradeMode);

            if (iceberg.CurrentStatus == ArbitrationStatus.On
                && iceberg.CurrentMode == tradeMode)
            {
                _scenarioTradeModes[scenario.UniqueName] = tradeMode;

                SendNewLogMessage(
                    $"Scenario {scenario.ScriptName}: open by MaxSpread. EntrySpread={recheckSpread}",
                    LogMessageType.System);
            }
            else
            {
                string status = iceberg.CurrentStatus.ToString();
                string mode = iceberg.CurrentMode.ToString();
                bool ready = iceberg.CheckTradingReady();

                SendNewLogMessage(
                    $"Scenario {scenario.ScriptName}: entry start failed. EntrySpread={recheckSpread}, mode={tradeMode}, statusAfterStart={status}, modeAfterStart={mode}, tradingReady={ready}",
                    LogMessageType.System);
            }
        }

        private void ProcessExitScenario(BondScenario scenario, ArbitrationMode tradeMode, decimal displaySpread)
        {
            ArbitrationIceberg iceberg = scenario.ArbitrationIceberg;

            if (scenario.State != BondScenarioState.Running
                && scenario.State != BondScenarioState.PauseOnlyClose)
            {
                return;
            }

            if (iceberg.CurrentStatus == ArbitrationStatus.On
                && iceberg.CurrentMode == ArbitrationMode.CloseScript)
            {
                return;
            }

            if (!TryCalculateExitSpread(scenario, tradeMode, out decimal exitSpread, out string reason))
            {
                LogBlockedReason(scenario, reason);
                return;
            }

            if (exitSpread > scenario.MinSpread)
            {
                return;
            }

            ResetBlockedReason(scenario);

            SendNewLogMessage(
                $"Scenario {scenario.ScriptName}: exit signal. ExitSpread={exitSpread}, DisplaySpread={displaySpread}, mode={tradeMode}, MinSpread={scenario.MinSpread}",
                LogMessageType.System);

            if (!TryCalculateExitSpread(scenario, tradeMode, out decimal recheckSpread, out reason)
                || recheckSpread > scenario.MinSpread - RecheckSpreadBufferPercent)
            {
                SendNewLogMessage(
                    $"Scenario {scenario.ScriptName}: exit cancelled before orders. RecheckSpread={recheckSpread}, required={scenario.MinSpread - RecheckSpreadBufferPercent}",
                    LogMessageType.System);
                return;
            }

            _scenarioTradeModes[scenario.UniqueName] = tradeMode;
            iceberg.Start(ArbitrationMode.CloseScript);

            if (iceberg.CurrentStatus == ArbitrationStatus.On
                && iceberg.CurrentMode == ArbitrationMode.CloseScript)
            {
                _closingScenarioNames.Add(scenario.UniqueName);

                SendNewLogMessage(
                    $"Scenario {scenario.ScriptName}: close by MinSpread. ExitSpread={recheckSpread}",
                    LogMessageType.System);
            }
        }

        private bool TryCalculateDisplaySpread(BondScenario scenario, ArbitrationMode mode, out decimal spread, out string reason)
        {
            return TryCalculateEntrySpread(scenario, mode, false, out spread, out reason);
        }

        private bool TryCalculateEntrySpread(BondScenario scenario, ArbitrationMode mode, out decimal spread, out string reason)
        {
            return TryCalculateEntrySpread(scenario, mode, true, out spread, out reason);
        }

        private bool TryCalculateEntrySpread(BondScenario scenario, ArbitrationMode mode, bool checkQuoteAge, out decimal spread, out string reason)
        {
            spread = 0;

            if (!TryGetQuoteSnapshot(scenario, checkQuoteAge, out LegQuote baseQuote, out LegQuote futuresQuote, out reason))
            {
                return false;
            }

            if (mode == ArbitrationMode.OpenBuyFirstSellSecond)
            {
                return TryCalculatePercent(futuresQuote.BidPrice, baseQuote.AskPrice, baseQuote.AskPrice, out spread, out reason);
            }

            if (mode == ArbitrationMode.OpenSellFirstBuySecond)
            {
                return TryCalculatePercent(baseQuote.BidPrice, futuresQuote.AskPrice, futuresQuote.AskPrice, out spread, out reason);
            }

            reason = "unsupported trade mode for entry spread calculation: " + mode;
            return false;
        }

        private bool TryCalculateExitSpread(BondScenario scenario, ArbitrationMode mode, out decimal spread, out string reason)
        {
            spread = 0;

            if (!TryGetQuoteSnapshot(scenario, true, out LegQuote baseQuote, out LegQuote futuresQuote, out reason))
            {
                return false;
            }

            if (mode == ArbitrationMode.OpenBuyFirstSellSecond)
            {
                return TryCalculatePercent(futuresQuote.AskPrice, baseQuote.BidPrice, baseQuote.BidPrice, out spread, out reason);
            }

            if (mode == ArbitrationMode.OpenSellFirstBuySecond)
            {
                return TryCalculatePercent(baseQuote.AskPrice, futuresQuote.BidPrice, futuresQuote.BidPrice, out spread, out reason);
            }

            reason = "unsupported trade mode for exit spread calculation: " + mode;
            return false;
        }

        private bool TryCalculatePercent(decimal firstPrice, decimal secondPrice, decimal denominator, out decimal spread, out string reason)
        {
            spread = 0;

            if (firstPrice <= 0
                || secondPrice <= 0
                || denominator <= 0)
            {
                reason = $"missing bid/ask price. first={firstPrice}, second={secondPrice}, denominator={denominator}";
                return false;
            }

            spread = (firstPrice - secondPrice) / denominator * 100;
            reason = null;
            return true;
        }

        private bool TryGetQuoteSnapshot(BondScenario scenario, bool checkQuoteAge, out LegQuote baseQuote, out LegQuote futuresQuote, out string reason)
        {
            baseQuote = null;
            futuresQuote = null;
            reason = null;

            lock (_quoteLocker)
            {
                if (!_scenarioQuotes.TryGetValue(scenario.UniqueName, out ScenarioQuoteState quoteState))
                {
                    reason = "no bid/ask quotes yet";
                    return false;
                }

                baseQuote = quoteState.BaseQuote.Clone();
                futuresQuote = quoteState.FuturesQuote.Clone();
            }

            if (!baseQuote.HasFullQuote)
            {
                reason = "base leg has no full bid/ask quote";
                return false;
            }

            if (!futuresQuote.HasFullQuote)
            {
                reason = "futures leg has no full bid/ask quote";
                return false;
            }

            int maxAgeSeconds = scenario.MaxQuoteAgeSeconds;

            if (checkQuoteAge && maxAgeSeconds > 0)
            {
                DateTime now = DateTime.UtcNow;

                if ((now - baseQuote.LastUpdateTime).TotalSeconds > maxAgeSeconds)
                {
                    reason = $"base leg quote is older than {maxAgeSeconds} sec";
                    return false;
                }

                if ((now - futuresQuote.LastUpdateTime).TotalSeconds > maxAgeSeconds)
                {
                    reason = $"futures leg quote is older than {maxAgeSeconds} sec";
                    return false;
                }
            }

            return true;
        }

        private bool IsOpenExecutionInProgress(ArbitrationIceberg iceberg)
        {
            return iceberg != null
                && iceberg.CurrentStatus == ArbitrationStatus.On
                && IsOpenMode(iceberg.CurrentMode)
                && !AreEnterStepsDone(iceberg);
        }

        private bool AreEnterStepsDone(ArbitrationIceberg iceberg)
        {
            return AreEnterStepsDone(iceberg.MainLegs)
                && AreEnterStepsDone(iceberg.SecondaryLegs);
        }

        private bool AreEnterStepsDone(List<ArbitrationLeg> legs)
        {
            if (legs == null || legs.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < legs.Count; i++)
            {
                ArbitrationLeg leg = legs[i];

                if (leg == null
                    || leg.EnterArbitrationSteps == null
                    || leg.EnterArbitrationSteps.Count == 0)
                {
                    return false;
                }

                for (int j = 0; j < leg.EnterArbitrationSteps.Count; j++)
                {
                    if (leg.EnterArbitrationSteps[j].Status != OrderStateType.Done)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private ScenarioQuoteState GetScenarioQuoteStateLocked(string scenarioName)
        {
            if (!_scenarioQuotes.TryGetValue(scenarioName, out ScenarioQuoteState quoteState))
            {
                quoteState = new ScenarioQuoteState();
                _scenarioQuotes[scenarioName] = quoteState;
            }

            return quoteState;
        }

        private LegQuote GetLegQuoteLocked(string scenarioName, bool isBaseLeg)
        {
            ScenarioQuoteState quoteState = GetScenarioQuoteStateLocked(scenarioName);

            return isBaseLeg
                ? quoteState.BaseQuote
                : quoteState.FuturesQuote;
        }

        private string GetLegSubscriptionKey(string scenarioName, bool isBaseLeg)
        {
            return scenarioName + (isBaseLeg ? "_base" : "_futures");
        }

        private bool TryGetScenarioTradeMode(BondScenario scenario, out ArbitrationMode tradeMode)
        {
            tradeMode = ArbitrationMode.OpenBuyFirstSellSecond;

            ArbitrationMode currentMode = scenario.ArbitrationIceberg.CurrentMode;

            if (IsOpenMode(currentMode))
            {
                tradeMode = currentMode;
                _scenarioTradeModes[scenario.UniqueName] = tradeMode;
                return true;
            }

            if (_scenarioTradeModes.TryGetValue(scenario.UniqueName, out tradeMode))
            {
                return true;
            }

            if (TryInferTradeModeFromPosition(scenario, out tradeMode))
            {
                _scenarioTradeModes[scenario.UniqueName] = tradeMode;
                return true;
            }

            return false;
        }

        private bool TryInferTradeModeFromPosition(BondScenario scenario, out ArbitrationMode tradeMode)
        {
            tradeMode = ArbitrationMode.OpenBuyFirstSellSecond;

            if (scenario == null
                || scenario.ArbitrationIceberg == null
                || scenario.ArbitrationIceberg.MainLegs == null)
            {
                return false;
            }

            for (int i = 0; i < scenario.ArbitrationIceberg.MainLegs.Count; i++)
            {
                ArbitrationLeg leg = scenario.ArbitrationIceberg.MainLegs[i];
                Position position = GetFirstActivePosition(leg);

                if (position == null)
                {
                    continue;
                }

                if (position.Direction == Side.Buy)
                {
                    tradeMode = ArbitrationMode.OpenBuyFirstSellSecond;
                    return true;
                }

                if (position.Direction == Side.Sell)
                {
                    tradeMode = ArbitrationMode.OpenSellFirstBuySecond;
                    return true;
                }
            }

            return false;
        }

        private bool IsOpenMode(ArbitrationMode mode)
        {
            return mode == ArbitrationMode.OpenBuyFirstSellSecond
                || mode == ArbitrationMode.OpenSellFirstBuySecond;
        }

        private bool TryCompletePendingClose(BondScenario scenario, bool hasPosition)
        {
            if (!_closingScenarioNames.Contains(scenario.UniqueName))
            {
                return false;
            }

            if (hasPosition)
            {
                return true;
            }

            _closingScenarioNames.Remove(scenario.UniqueName);
            _scenarioTradeModes.Remove(scenario.UniqueName);
            scenario.CompletedCycles++;
            scenario.Save();

            SendNewLogMessage(
                $"Scenario {scenario.ScriptName}: cycle completed. Completed cycles: {scenario.CompletedCycles}",
                LogMessageType.System);

            if (scenario.State == BondScenarioState.PauseOnlyClose
                || scenario.State == BondScenarioState.StoppingNow)
            {
                SetScenarioState(
                    scenario,
                    BondScenarioState.Stopped,
                    $"Scenario {scenario.ScriptName}: close completed in {scenario.State}. State switched to Stopped.");
            }

            if (IsCyclesLimitReached(scenario))
            {
                string message = $"Scenario {scenario.ScriptName}: cycles limit reached. Completed={scenario.CompletedCycles}, limit={scenario.CyclesCount}. State switched to Stopped.";

                if (scenario.State == BondScenarioState.Stopped)
                {
                    SendNewLogMessage(message, LogMessageType.System);
                }
                else
                {
                    SetScenarioState(scenario, BondScenarioState.Stopped, message);
                }
            }

            return true;
        }

        private bool IsCyclesLimitReached(BondScenario scenario)
        {
            return scenario != null
                   && scenario.CyclesCount > 0
                   && scenario.CompletedCycles >= scenario.CyclesCount;
        }

        private void SetScenarioState(BondScenario scenario, BondScenarioState state, string message)
        {
            if (scenario == null)
            {
                return;
            }

            if (scenario.State == state)
            {
                return;
            }

            scenario.State = state;
            scenario.Save();
            ResetBlockedReason(scenario);

            if (!string.IsNullOrEmpty(message))
            {
                SendNewLogMessage(message, LogMessageType.System);
            }
        }

        private void LogBlockedReason(BondScenario scenario, string reason)
        {
            if (scenario == null || string.IsNullOrEmpty(reason))
            {
                return;
            }

            if (_scenarioLastBlockedReason.TryGetValue(scenario.UniqueName, out string lastReason)
                && lastReason == reason)
            {
                return;
            }

            _scenarioLastBlockedReason[scenario.UniqueName] = reason;

            SendNewLogMessage(
                $"Scenario {scenario.ScriptName}: trading blocked. {reason}",
                LogMessageType.System);
        }

        private void ResetBlockedReason(BondScenario scenario)
        {
            if (scenario == null)
            {
                return;
            }

            if (_scenarioLastBlockedReason.Remove(scenario.UniqueName))
            {
                SendNewLogMessage(
                    $"Scenario {scenario.ScriptName}: trading conditions restored.",
                    LogMessageType.System);
            }
        }

        private bool IsScenarioConnected(ArbitrationIceberg iceberg)
        {
            if (iceberg == null
                || iceberg.MainLegs == null
                || iceberg.MainLegs.Count == 0
                || iceberg.SecondaryLegs == null
                || iceberg.SecondaryLegs.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < iceberg.MainLegs.Count; i++)
            {
                if (iceberg.MainLegs[i] == null
                    || iceberg.MainLegs[i].BotTab == null)
                {
                    return false;
                }
            }

            for (int i = 0; i < iceberg.SecondaryLegs.Count; i++)
            {
                if (iceberg.SecondaryLegs[i] == null
                    || iceberg.SecondaryLegs[i].BotTab == null)
                {
                    return false;
                }
            }

            return true;
        }

        private bool HasActivePosition(ArbitrationIceberg iceberg)
        {
            for (int i = 0; i < iceberg.MainLegs.Count; i++)
            {
                if (HasActivePositionInLeg(iceberg.MainLegs[i]))
                {
                    return true;
                }
            }

            for (int i = 0; i < iceberg.SecondaryLegs.Count; i++)
            {
                if (HasActivePositionInLeg(iceberg.SecondaryLegs[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasActivePositionInLeg(ArbitrationLeg leg)
        {
            return GetFirstActivePosition(leg) != null;
        }

        private Position GetFirstActivePosition(ArbitrationLeg leg)
        {
            if (leg == null || leg.BotTab == null || leg.BotTab.PositionsOpenAll == null)
            {
                return null;
            }

            List<Position> positions = leg.BotTab.PositionsOpenAll;

            for (int i = 0; i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position != null
                    && position.State != PositionStateType.Done
                    && position.State != PositionStateType.OpeningFail
                    && position.OpenVolume > 0)
                {
                    return position;
                }
            }

            return null;
        }

        private class ScenarioQuoteState
        {
            public BondScenario Scenario;
            public readonly LegQuote BaseQuote = new LegQuote();
            public readonly LegQuote FuturesQuote = new LegQuote();
        }

        private class LegQuote
        {
            public decimal BidPrice;
            public decimal BidVolume;
            public decimal AskPrice;
            public decimal AskVolume;
            public DateTime LastUpdateTime = DateTime.MinValue;
            public bool HasBid;
            public bool HasAsk;

            public bool HasFullQuote => HasBid && HasAsk && BidPrice > 0 && AskPrice > 0;

            public bool Update(bool hasBid, decimal bidPrice, decimal bidVolume, bool hasAsk, decimal askPrice, decimal askVolume)
            {
                bool changed = false;

                if (hasBid
                    && (!HasBid || BidPrice != bidPrice || BidVolume != bidVolume))
                {
                    HasBid = true;
                    BidPrice = bidPrice;
                    BidVolume = bidVolume;
                    changed = true;
                }

                if (hasAsk
                    && (!HasAsk || AskPrice != askPrice || AskVolume != askVolume))
                {
                    HasAsk = true;
                    AskPrice = askPrice;
                    AskVolume = askVolume;
                    changed = true;
                }

                if (changed)
                {
                    LastUpdateTime = DateTime.UtcNow;
                }

                return changed;
            }

            public bool UpdatePrices(decimal bid, decimal ask)
            {
                bool hasBid = bid > 0;
                bool hasAsk = ask > 0;

                return Update(hasBid, bid, BidVolume, hasAsk, ask, AskVolume);
            }

            public LegQuote Clone()
            {
                return new LegQuote
                {
                    BidPrice = BidPrice,
                    BidVolume = BidVolume,
                    AskPrice = AskPrice,
                    AskVolume = AskVolume,
                    LastUpdateTime = LastUpdateTime,
                    HasBid = HasBid,
                    HasAsk = HasAsk
                };
            }
        }

        private class LegSubscription
        {
            public readonly BotTabSimple Tab;
            private readonly Action<MarketDepth> _marketDepthHandler;
            private readonly Action<decimal, decimal> _bestBidAskHandler;

            public LegSubscription(BotTabSimple tab, Action<MarketDepth> marketDepthHandler, Action<decimal, decimal> bestBidAskHandler)
            {
                Tab = tab;
                _marketDepthHandler = marketDepthHandler;
                _bestBidAskHandler = bestBidAskHandler;
            }

            public void Subscribe()
            {
                Tab.MarketDepthUpdateEvent += _marketDepthHandler;
                Tab.BestBidAskChangeEvent += _bestBidAskHandler;
            }

            public void Unsubscribe()
            {
                Tab.MarketDepthUpdateEvent -= _marketDepthHandler;
                Tab.BestBidAskChangeEvent -= _bestBidAskHandler;
            }
        }
    }
}
