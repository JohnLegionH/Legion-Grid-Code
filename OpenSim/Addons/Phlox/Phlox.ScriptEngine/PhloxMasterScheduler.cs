/*
 * Legion Grid — Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon MasterScheduler.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 for Legion Grid / OpenSim 0.9.3 .NET 8
 */

using System;
using System.Reflection;
using OpenSim.Framework;
using System.Threading;
using log4net;

namespace Phlox.ScriptEngine
{
    internal class PhloxMasterScheduler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly PhloxExecutionScheduler m_ExeScheduler;
        private readonly PhloxScriptLoader m_ScriptLoader;
        private readonly ManualResetEvent m_ActionEvent = new ManualResetEvent(false);
        private Thread m_Thread;
        private volatile bool m_Stop = false;

        public bool IsRunning => !m_Stop;

        public PhloxMasterScheduler(PhloxExecutionScheduler exeScheduler, PhloxScriptLoader scriptLoader)
        {
            m_ExeScheduler = exeScheduler;
            m_ScriptLoader = scriptLoader;
        }

        public void Start()
        {
            m_Thread = new Thread(WorkLoop)
            {
                Name = "PhloxMasterScheduler",
                Priority = PhloxEngine.SUBTASK_PRIORITY,
                IsBackground = true
            };
            m_Thread.Start();
        }

        public void Stop()
        {
            m_Stop = true;
            WorkArrived();
            m_Thread?.Join(5000);
            m_ScriptLoader.Stop();
            m_ExeScheduler.Stop();
        }

        public void WorkArrived()
        {
            m_ActionEvent.Set();
        }

        private void WorkLoop()
        {
            const int MAX_CRASHES = 10;
            int crashCount = 0;

            while (!m_Stop)
            {
                try
                {
                    while (!m_Stop)
                    {
                        WorkStatus exeStatus = m_ExeScheduler.DoWork();
                        WorkStatus loadStatus = m_ScriptLoader.DoWork();

                        if (!exeStatus.WorkIsPending && !loadStatus.WorkIsPending)
                        {
                            ulong wakeAt = Math.Min(exeStatus.NextWakeUpTime, loadStatus.NextWakeUpTime);

                            if (wakeAt != ulong.MaxValue)
                            {
                                long waitMs = (long)wakeAt - (long)(ulong)Util.EnvironmentTickCount();
                                if (waitMs > 0)
                                {
                                    m_ActionEvent.WaitOne((int)Math.Min(waitMs, int.MaxValue));
                                    m_ActionEvent.Reset();
                                }
                            }
                            else
                            {
                                m_ActionEvent.WaitOne();
                                m_ActionEvent.Reset();
                            }
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    // Normal shutdown — don't restart
                    break;
                }
                catch (Exception e)
                {
                    crashCount++;
                    if (crashCount >= MAX_CRASHES)
                    {
                        m_log.ErrorFormat(
                            "[PhloxMaster]: CRITICAL — master scheduler crashed {0} times, script engine disabled. {1}",
                            crashCount, e);
                        m_Stop = true;
                        break;
                    }

                    m_log.ErrorFormat(
                        "[PhloxMaster]: Master scheduler exception (crash #{0}/{1}), restarting in 1s. {2}",
                        crashCount, MAX_CRASHES, e);

                    // Brief pause before restart to avoid tight crash loops
                    Thread.Sleep(1000);
                }
            }
        }
    }
}
