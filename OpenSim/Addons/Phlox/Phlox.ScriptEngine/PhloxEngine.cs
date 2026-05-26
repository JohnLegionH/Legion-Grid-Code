/*
 * Legion Grid — Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon EngineInterface.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 for Legion Grid / OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Services.Interfaces;

[assembly: Addin("PhloxEngine", OpenSim.VersionInfo.AssemblyVersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.AssemblyVersionNumber)]

namespace Phlox.ScriptEngine
{
    public delegate void WorkArrivedDelegate();

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "PhloxEngine")]
    public class PhloxEngine : INonSharedRegionModule, IScriptEngine, IScriptModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const ThreadPriority SUBTASK_PRIORITY = ThreadPriority.Lowest;

        private Scene m_Scene;
        private IConfigSource m_ConfigSource;
        private IConfig m_Config;
        private bool m_Enabled = false;

        private PhloxScriptLoader m_ScriptLoader;
        private PhloxExecutionScheduler m_ExeScheduler;
        private PhloxMasterScheduler m_MasterScheduler;
        private InWorldz.Phlox.Types.SupportedEventList m_EventList = new InWorldz.Phlox.Types.SupportedEventList();

        internal PhloxListenManager ListenManager { get; private set; }
        internal AsyncCommandManager AsyncCommands { get; private set; }
		internal StateManager StateManager { get; private set; }

        #region INonSharedRegionModule

        public string Name => "InWorldz.Phlox";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            m_ConfigSource = config;
            m_Config = config.Configs["InWorldz.Phlox"];
            if (m_Config == null)
            {
                m_log.Info("[PhloxEngine]: No config section [InWorldz.Phlox] found, disabled");
                return;
            }
            m_Enabled = m_Config.GetBoolean("Enabled", false);
            m_log.InfoFormat("[PhloxEngine]: Enabled = {0}", m_Enabled);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled) return;
            m_Scene = scene;
            m_Scene.RegisterModuleInterface<IScriptModule>(this);
            m_Scene.StackModuleInterface<IScriptModule>(this);
            m_log.InfoFormat("[PhloxEngine]: Added to region {0}", scene.RegionInfo.RegionName);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled) return;

            // IWorldComm must be resolved here (not AddRegion) because
            // WorldCommModule may not have registered yet during AddRegion.
            IWorldComm worldComm = scene.RequestModuleInterface<IWorldComm>();
            if (worldComm == null)
            {
                m_log.Error("[PhloxEngine]: No IWorldComm module found, script engine disabled");
                m_Enabled = false;
                return;
            }
            m_ExeScheduler = new PhloxExecutionScheduler(WorkArrived, this, worldComm);
            m_ScriptLoader = new PhloxScriptLoader(scene.AssetService, m_ExeScheduler, WorkArrived, this);
            m_MasterScheduler = new PhloxMasterScheduler(m_ExeScheduler, m_ScriptLoader);
            ListenManager = new PhloxListenManager(m_ExeScheduler);
            AsyncCommands = new AsyncCommandManager(this);
            StateManager = new StateManager(this);
            StateManager.Start();
            m_MasterScheduler.Start();

            m_Scene.EventManager.OnRezScript += OnRezScript;
            m_Scene.EventManager.OnRemoveScript += OnRemoveScript;
            m_Scene.EventManager.OnScriptReset += OnScriptReset;
            m_Scene.EventManager.OnStartScript += OnStartScript;
            m_Scene.EventManager.OnStopScript += OnStopScript;
            m_Scene.EventManager.OnGetScriptRunning += OnGetScriptRunning;
            m_Scene.EventManager.OnChatFromWorld += OnChatFromWorld;
            m_Scene.EventManager.OnChatFromClient += OnChatFromClient;
            m_Scene.EventManager.OnObjectGrab += OnObjectGrab;
            m_Scene.EventManager.OnObjectGrabbing += OnObjectGrabbing;
            m_Scene.EventManager.OnObjectDeGrab += OnObjectDeGrab;
            m_Scene.EventManager.OnScriptChangedEvent += OnScriptChangedEvent;
            m_Scene.EventManager.OnScriptControlEvent += OnScriptControlEvent;
			m_Scene.EventManager.OnShutdown += OnShutdown;
            m_Scene.EventManager.OnScriptColliderStart     += OnScriptColliderStart;
            m_Scene.EventManager.OnScriptColliding         += OnScriptColliding;
            m_Scene.EventManager.OnScriptCollidingEnd      += OnScriptCollidingEnd;
            m_Scene.EventManager.OnScriptLandColliderStart += OnScriptLandColliderStart;
            m_Scene.EventManager.OnScriptLandColliding     += OnScriptLandColliding;
            m_Scene.EventManager.OnScriptLandColliderEnd   += OnScriptLandColliderEnd;
            m_log.InfoFormat("[PhloxEngine]: Region loaded {0}", scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled) return;
            m_Scene.EventManager.OnRezScript -= OnRezScript;
            m_Scene.EventManager.OnRemoveScript -= OnRemoveScript;
            m_Scene.EventManager.OnScriptReset -= OnScriptReset;
            m_Scene.EventManager.OnStartScript -= OnStartScript;
            m_Scene.EventManager.OnStopScript -= OnStopScript;
            m_Scene.EventManager.OnGetScriptRunning -= OnGetScriptRunning;
            m_Scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
            m_Scene.EventManager.OnChatFromClient -= OnChatFromClient;
            m_Scene.EventManager.OnObjectGrab -= OnObjectGrab;
            m_Scene.EventManager.OnObjectGrabbing -= OnObjectGrabbing;
            m_Scene.EventManager.OnObjectDeGrab -= OnObjectDeGrab;
            m_Scene.EventManager.OnScriptChangedEvent -= OnScriptChangedEvent;
            m_Scene.EventManager.OnScriptControlEvent -= OnScriptControlEvent;
            m_Scene.EventManager.OnScriptLandColliderEnd   -= OnScriptLandColliderEnd;
            m_Scene.EventManager.OnScriptLandColliding     -= OnScriptLandColliding;
            m_Scene.EventManager.OnScriptLandColliderStart -= OnScriptLandColliderStart;
            m_Scene.EventManager.OnScriptCollidingEnd      -= OnScriptCollidingEnd;
            m_Scene.EventManager.OnScriptColliding         -= OnScriptColliding;
            m_Scene.EventManager.OnScriptColliderStart     -= OnScriptColliderStart;
            m_MasterScheduler?.Stop();
            AsyncCommands?.Shutdown();
            m_Scene = null;
			
			StateManager?.Stop();
			StateManager = null;
        }

        public void Close() { }

        #endregion

        private void WorkArrived()
        {
            m_MasterScheduler?.WorkArrived();
        }

        #region Scene event handlers

        private void OnRezScript(uint localID, UUID itemID, string script,
            int startParam, bool postOnRez, string engine, int stateSource)
        {
            if (engine != Name) return;

            SceneObjectPart part = m_Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.ErrorFormat("[PhloxEngine]: OnRezScript: prim {0} not found for script {1}", localID, itemID);
                return;
            }

            m_log.DebugFormat("[PhloxEngine]: OnRezScript {0} in prim {1}", itemID, localID);

            m_ScriptLoader.PostLoadRequest(new PhloxLoadRequest
            {
                LocalID = localID,
                ItemID = itemID,
                ScriptText = script,
                StartParam = startParam,
                PostOnRez = postOnRez,
                StateSource = stateSource,
                Prim = part,
            });
        }

        private void OnRemoveScript(uint localID, UUID itemID)
        {
            m_log.DebugFormat("[PhloxEngine]: OnRemoveScript {0}", itemID);
            m_ScriptLoader.PostUnloadRequest(localID, itemID);
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            m_ExeScheduler?.ResetScript(itemID);
        }

		private void OnShutdown()
        {
            m_log.Info("[PhloxEngine]: Shutdown event, flushing script state");
            StateManager?.Stop();
            StateManager = null;
        }
		
        private void OnStartScript(uint localID, UUID itemID)
        {
            m_ExeScheduler?.ChangeEnabledStatus(itemID, true);
        }

        private void OnStopScript(uint localID, UUID itemID)
        {
            m_ExeScheduler?.ChangeEnabledStatus(itemID, false);
        }

        private void OnGetScriptRunning(IClientAPI controllingClient, UUID objectID, UUID itemID)
        {
            if (m_ExeScheduler == null) return;
            // TODO: implement ScriptRunningReply when LindenCaps reference is available
        }

        private void OnChatFromWorld(object sender, OSChatMessage chat)
        {
            ListenManager?.DeliverChat(chat.Channel, chat.From, chat.SenderUUID, chat.Message);
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            // HandlerScriptDialogReply (LLClientView) sets chat.Sender but leaves
            // chat.SenderUUID at its UUID.Zero default.  A key-filtered llListen
            // (llListen(chan, "", ownerKey, "")) would never match because
            // DeliverChat compares FilterKey against speakerKey == UUID.Zero.
            // Fall back to the client's AgentId so dialog-button replies reach scripts.
            UUID speakerKey = chat.SenderUUID;
            if (speakerKey == UUID.Zero && chat.Sender != null)
                speakerKey = chat.Sender.AgentId;
            string speakerName = chat.From;
            if (string.IsNullOrEmpty(speakerName) && chat.Sender != null)
                speakerName = chat.Sender.Name;
            ListenManager?.DeliverChat(chat.Channel, speakerName, speakerKey, chat.Message);
        }

        // ── Touch events ───────────────────────────────────────────────────────

        private void OnObjectGrab(uint localID, uint originalID, Vector3 offsetPos,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(part, remoteClient, offsetPos, surfaceArgs);

            PostTouchEvent(part.ParentGroup,
                InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
                "touch_start", dp);
        }

        private void OnObjectGrabbing(uint localID, uint originalID, Vector3 offsetPos,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(part, remoteClient, offsetPos, surfaceArgs);

            PostTouchEvent(part.ParentGroup,
                InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH,
                "touch", dp);
        }

        private void OnObjectDeGrab(uint localID, uint originalID,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(part, remoteClient, Vector3.Zero, surfaceArgs);

            PostTouchEvent(part.ParentGroup,
                InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_END,
                "touch_end", dp);
        }

        /// <summary>
        /// Builds a DetectParams for a touch event from the grabbing avatar's data.
        /// DetectParams uses LSL_Types for position/rotation/velocity, and exposes
        /// touch surface data only via the SurfaceTouchArgs write-only setter.
        /// </summary>
        private DetectParams BuildTouchDetectParams(SceneObjectPart part,
            IClientAPI remoteClient, Vector3 offsetPos, SurfaceTouchEventArgs surfaceArgs)
        {
            ScenePresence sp = m_Scene?.GetScenePresence(remoteClient.AgentId);

            var dp = new DetectParams
            {
                Key     = remoteClient.AgentId,
                Name    = remoteClient.Name,
                Owner   = remoteClient.AgentId,
                Group   = UUID.Zero,
                Type    = DetectParams.AGENT,
                LinkNum = part.LinkNum,
                OffsetPos = new LSL_Types.Vector3(
                    offsetPos.X, offsetPos.Y, offsetPos.Z),
                Position = sp != null
                    ? new LSL_Types.Vector3(
                        sp.AbsolutePosition.X,
                        sp.AbsolutePosition.Y,
                        sp.AbsolutePosition.Z)
                    : new LSL_Types.Vector3(),
                Velocity = sp != null
                    ? new LSL_Types.Vector3(
                        sp.Velocity.X,
                        sp.Velocity.Y,
                        sp.Velocity.Z)
                    : new LSL_Types.Vector3(),
                Rotation = sp != null
                    ? new LSL_Types.Quaternion(
                        sp.Rotation.X,
                        sp.Rotation.Y,
                        sp.Rotation.Z,
                        sp.Rotation.W)
                    : new LSL_Types.Quaternion(),
            };

            // SurfaceTouchArgs is a write-only setter that populates all the
            // read-only Touch* properties (TouchFace, TouchPos, TouchNormal, etc.)
            // Passing null resets them to safe defaults (-1 face, zero vectors).
            dp.SurfaceTouchArgs = surfaceArgs;

            return dp;
        }

        /// <summary>
        /// Posts a touch event to every script in the linkset that has that
        /// event handler registered in its current state.
        /// </summary>
        private void PostTouchEvent(SceneObjectGroup group,
            InWorldz.Phlox.Types.SupportedEventList.Events eventType,
            string eventName, DetectParams dp)
        {
            if (group == null || group.IsDeleted) return;

            var parms = new EventParams(
                eventName,
                new object[] { 1 },
                new DetectParams[] { dp });

            // Post to every prim in the linkset — scripts that don't handle
            // the event will have it dropped by FindEventHandler in the scheduler.
            foreach (SceneObjectPart part in group.Parts)
                PostObjectEvent(part.LocalId, parms);
        }

        // ── Changed event ──────────────────────────────────────────────────────

        // CHANGED_* constants matching LSL spec
        private const int CHANGED_INVENTORY  = 0x1;
        private const int CHANGED_COLOR      = 0x2;
        private const int CHANGED_SHAPE      = 0x4;
        private const int CHANGED_SCALE      = 0x8;
        private const int CHANGED_TEXTURE    = 0x10;
        private const int CHANGED_LINK       = 0x20;
        private const int CHANGED_ALLOWED_DROP = 0x40;
        private const int CHANGED_OWNER      = 0x80;
        private const int CHANGED_REGION     = 0x100;
        private const int CHANGED_TELEPORT   = 0x200;
        private const int CHANGED_REGION_START = 0x400;
        private const int CHANGED_MEDIA      = 0x800;

        private void OnScriptChangedEvent(uint localID, uint change, object data)
        {
            // Delivers changed() events fired by OpenSim's own infrastructure:
            // CHANGED_LINK (sit/stand/link/unlink), CHANGED_SCALE, CHANGED_SHAPE, etc.
            // The localID is the specific part that changed — post only to that part's scripts.
            var parms = new EventParams("changed",
                new object[] { (int)change },
                new DetectParams[0]);
            PostObjectEvent(localID, parms);
        }

        // ── Collision events ───────────────────────────────────────────────────

        private void OnScriptColliderStart(uint localID, ColliderArgs col)
        {
            int dc = col.Colliders.Count;
            if (dc == 0) return;
            DetectParams[] det = new DetectParams[dc];
            int i = 0;
            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                det[i++] = d;
            }
            PostObjectEvent(localID, new EventParams("collision_start", new object[] { dc }, det));
        }

        private void OnScriptColliding(uint localID, ColliderArgs col)
        {
            int dc = col.Colliders.Count;
            if (dc == 0) return;
            DetectParams[] det = new DetectParams[dc];
            int i = 0;
            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                det[i++] = d;
            }
            PostObjectEvent(localID, new EventParams("collision", new object[] { dc }, det));
        }

        private void OnScriptCollidingEnd(uint localID, ColliderArgs col)
        {
            int dc = col.Colliders.Count;
            if (dc == 0) return;
            DetectParams[] det = new DetectParams[dc];
            int i = 0;
            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                det[i++] = d;
            }
            PostObjectEvent(localID, new EventParams("collision_end", new object[] { dc }, det));
        }

        // ── Land collision events ──────────────────────────────────────────────

        private void OnScriptLandColliderStart(uint localID, ColliderArgs col)
        {
            foreach (DetectedObject detobj in col.Colliders)
                PostObjectEvent(localID, new EventParams(
                    "land_collision_start", new object[] { detobj.posVector }, new DetectParams[0]));
        }

        private void OnScriptLandColliding(uint localID, ColliderArgs col)
        {
            foreach (DetectedObject detobj in col.Colliders)
                PostObjectEvent(localID, new EventParams(
                    "land_collision", new object[] { detobj.posVector }, new DetectParams[0]));
        }

        private void OnScriptLandColliderEnd(uint localID, ColliderArgs col)
        {
            foreach (DetectedObject detobj in col.Colliders)
                PostObjectEvent(localID, new EventParams(
                    "land_collision_end", new object[] { detobj.posVector }, new DetectParams[0]));
        }

        #endregion

        #region IScriptModule

        public string ScriptEngineName => Name;

        public event ScriptRemoved OnScriptRemoved;
        public event ObjectRemoved OnObjectRemoved;

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;

        public bool PostScriptEvent(UUID itemID, string name, object[] args)
            => PostScriptEvent(itemID, new EventParams(name, args, null));

        public bool PostObjectEvent(UUID localID, string name, object[] args)
            => false;

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            if (m_ExeScheduler == null) return false;

            if (!m_EventList.HasEventByName(parms.EventName)) return false;
            InWorldz.Phlox.Types.FunctionSig eventInfo = m_EventList.GetEventByName(parms.EventName);

            InWorldz.Phlox.VM.DetectVariables[] detectVars = ConvertDetectParams(parms.DetectParams);

            var evt = new InWorldz.Phlox.VM.PostedEvent
            {
                EventType = (InWorldz.Phlox.Types.SupportedEventList.Events)eventInfo.TableIndex,
                Args = parms.Params,
                DetectVars = detectVars
            };
            evt.Normalize();
            m_ExeScheduler.PostEvent(itemID, evt);
            return true;
        }

        private void OnScriptControlEvent(UUID itemID, UUID agentID, uint held, uint change)
        {
            PostScriptEvent(itemID, new EventParams(
                "control", new object[] {
                    agentID.ToString(),
                    (int)held,
                    (int)change },
                null));
        }

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            SceneObjectPart part = World?.GetSceneObjectPart(localID);
            if (part == null) return false;

            // Defer the inventory snapshot and event dispatch to a thread pool work item.
            //
            // This method can be invoked synchronously from inside callers that hold a
            // write lock on part.TaskInventory's underlying ReaderWriterLockSlim — most
            // notably OpenSim.Region.Framework.Scenes.EventManager.TriggerOnScriptChangedEvent,
            // which fires when prim inventory mutates (script add/remove, notecard save).
            //
            // TaskInventoryDictionary.Clone() acquires a read lock on that same
            // ReaderWriterLockSlim internally. In the default (non-recursive) policy,
            // ReaderWriterLockSlim throws LockRecursionException when the same thread
            // attempts a read while already holding the write lock. That manifested as:
            //   "A read lock may not be acquired with the write lock held in this mode"
            // inside [EVENT MANAGER]: Delegate for TriggerOnScriptChangedEvent failed.
            //
            // Hopping to the thread pool guarantees the original caller has released
            // its write lock by the time Clone() runs. The trade-off: this method now
            // returns true *before* events are actually delivered. No current caller
            // (OnScriptChangedEvent, OnSceneObjectPartUpdated, PostTouchEvent,
            //  PostObjectLinksetDataEvent) inspects the return value, so this is safe.
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    TaskInventoryDictionary scripts;
                    lock (part.TaskInventory)
                        scripts = (TaskInventoryDictionary)part.TaskInventory.Clone();

                    foreach (var kvp in scripts)
                    {
                        if (kvp.Value.Type == (int)AssetType.LSLText || kvp.Value.Type == 10)
                            PostScriptEvent(kvp.Value.ItemID, parms);
                    }
                }
                catch (Exception e)
                {
                    // Unhandled exceptions in ThreadPool work items terminate the
                    // process on .NET Core/5+. Swallow and log instead.
                    m_log.ErrorFormat(
                        "[PhloxEngine]: PostObjectEvent deferred dispatch failed for localID {0}: {1}",
                        localID, e);
                }
            }, null);

            return true;
        }

        public bool PostObjectLinksetDataEvent(uint localID, int action,
            ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            var parms = new EventParams("linkset_data",
                new object[] { action, name.ToString(), value.ToString() }, null);
            return PostObjectEvent(localID, parms);
        }

        public System.Collections.ArrayList GetScriptErrors(UUID itemID) => new System.Collections.ArrayList();
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public void SaveAllState() { }
        public void StartProcessing() { }
        public float GetScriptExecutionTime(List<UUID> itemIDs) => 0f;
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => new Dictionary<uint, float>();
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
        public int GetScriptsMemory(List<UUID> itemIDs) => 0;
        public ICollection<ScriptTopStatsData> GetTopObjectStats(float minTime, int maxCount,
            out float totalTime, out float memUsage)
        {
            totalTime = 0f; memUsage = 0f;
            return new List<ScriptTopStatsData>();
        }

        #endregion

        #region IScriptEngine

        public Scene World => m_Scene;
        public IScriptModule ScriptModule => this;
        public IConfig Config => m_Config;
        public IConfigSource ConfigSource => m_ConfigSource;
        public string ScriptEnginePath => "ScriptEngines/Phlox";
        public string ScriptClassName => "PhloxScript";
        public string ScriptBaseClassName => "InWorldz.Phlox.VM.Interpreter";
        public string[] ScriptReferencedAssemblies => Array.Empty<string>();
        public ParameterInfo[] ScriptBaseClassParameters => null;

        public IScriptWorkItem QueueEventHandler(object parms) => null;
        public void CancelScriptEvent(UUID itemID, string eventName) { }

        public DetectParams GetDetectParams(UUID item, int number) => null;
        public void SetMinEventDelay(UUID itemID, double delay) { }
        public int GetStartParameter(UUID itemID) => 0;

        public void SetScriptState(UUID itemID, bool state, bool self)
            => m_ExeScheduler?.ChangeEnabledStatus(itemID, state);

        public bool GetScriptState(UUID itemID)
            => m_ExeScheduler?.GetScriptRunning(itemID) ?? false;

        public void SetState(UUID itemID, string newState) { }

        public void ApiResetScript(UUID itemID)
            => m_ExeScheduler?.ResetNow(itemID);

        public void ResetScript(UUID itemID)
            => m_ExeScheduler?.ResetScript(itemID);

        public void SleepScript(UUID itemID, int delay) { }

        public IScriptApi GetApi(UUID itemID, string name) => null;

        #endregion

        #region Internal helpers used by LSLSystemAPI

        /// <summary>
        /// Called by LSLSystemAPI.state() to trigger a state change.
        /// State changes are driven by the VM via OnStateChg — this is a no-op at the engine level.
        /// </summary>
        public void SetStateInternal(UUID itemID, string newState) { }

        /// <summary>
        /// Called by LSLSystemAPI when a long-running syscall completes.
        /// </summary>
        public void SysReturn(UUID itemId, object retValue, int delay)
            => m_ExeScheduler?.PostSyscallReturn(itemId, retValue, delay);

        /// <summary>
        /// Called by LSLSystemAPI.llSetTimerEvent.
        /// </summary>
        public void SetTimerEvent(uint localID, UUID itemID, float sec)
            => m_ExeScheduler?.SetTimer(itemID, sec);

        #endregion

        #region Helpers

        private InWorldz.Phlox.VM.DetectVariables[] ConvertDetectParams(DetectParams[] parms)
        {
            if (parms == null) return Array.Empty<InWorldz.Phlox.VM.DetectVariables>();

            var result = new InWorldz.Phlox.VM.DetectVariables[parms.Length];
            for (int i = 0; i < parms.Length; i++)
            {
                result[i] = new InWorldz.Phlox.VM.DetectVariables
                {
                    Key          = parms[i].Key.ToString(),
                    Group        = parms[i].Group.ToString(),
                    LinkNumber   = parms[i].LinkNum,
                    Name         = parms[i].Name,
                    Owner        = parms[i].Owner.ToString(),
                    Pos          = parms[i].Position,
                    Rot          = parms[i].Rotation,
                    Type         = parms[i].Type,
                    Vel          = parms[i].Velocity,
                    Grab         = parms[i].OffsetPos,
                    TouchBinormal= parms[i].TouchBinormal,
                    TouchFace    = parms[i].TouchFace,
                    TouchNormal  = parms[i].TouchNormal,
                    TouchPos     = parms[i].TouchPos,
                    TouchST      = parms[i].TouchST,
                    TouchUV      = parms[i].TouchUV,
                };
            }
            return result;
        }

        #endregion
    }
}
