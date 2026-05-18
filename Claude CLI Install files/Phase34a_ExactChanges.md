# Phase 34a — Exact Code Changes

All changes below use FIND → REPLACE. Find the exact "OLD" text, replace with the "NEW" text.

---

## File 1: BotManager.cs
**Location:** `Region/OptionalModules/World/NPC/BotManager.cs`

### Change 1.1 — Add using statements

**FIND:**
```csharp
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.World.NPC
```

**REPLACE WITH:**
```csharp
using OpenSim.Services.Interfaces;
using System.IO;
using Microsoft.Data.Sqlite;

namespace OpenSim.Region.OptionalModules.World.NPC
```

### Change 1.2 — Add fields (m_config, m_persistence, PersistenceManager)

**FIND:**
```csharp
        private readonly List<Scene> m_scenes = new List<Scene>();
        private INPCModule m_npcModule;
        private bool m_enabled;
```

**REPLACE WITH:**
```csharp
        private readonly List<Scene> m_scenes = new List<Scene>();
        private INPCModule m_npcModule;
        private bool m_enabled;
        private IConfigSource m_config;
        private BotPersistenceManager m_persistence;

        /// <summary>
        /// Public accessor for script API to reach persistence manager.
        /// </summary>
        public BotPersistenceManager PersistenceManager => m_persistence;
```

### Change 1.3 — Store IConfigSource in Initialise

**FIND:**
```csharp
        public void Initialise(IConfigSource source)
        {
            // BotManager is enabled if NPC module is enabled
            IConfig config = source.Configs["NPC"];
            m_enabled = config != null && config.GetBoolean("Enabled", true);
        }
```

**REPLACE WITH:**
```csharp
        public void Initialise(IConfigSource source)
        {
            m_config = source;
            // BotManager is enabled if NPC module is enabled
            IConfig config = source.Configs["NPC"];
            m_enabled = config != null && config.GetBoolean("Enabled", true);
        }
```

### Change 1.4 — Initialize persistence in RegionLoaded

**FIND:**
```csharp
        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled) return;
            m_npcModule = scene.RequestModuleInterface<INPCModule>();
            if (m_npcModule == null)
            {
                m_log.Warn("[BotManager] INPCModule not found -- bot functions will be unavailable.");
                m_enabled = false;
            }
        }
```

**REPLACE WITH:**
```csharp
        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled) return;
            m_npcModule = scene.RequestModuleInterface<INPCModule>();
            if (m_npcModule == null)
            {
                m_log.Warn("[BotManager] INPCModule not found -- bot functions will be unavailable.");
                m_enabled = false;
                return;
            }

            // Initialize bot persistence
            m_persistence = new BotPersistenceManager();
            m_persistence.Initialize(scene, this, m_config);

            // Load and respawn persistent bots (staggered)
            m_persistence.LoadPersistentBots();
            m_persistence.StartTimers();

            // Register console commands
            RegisterConsoleCommands(scene);
        }
```

### Change 1.5 — Hook persistence shutdown into RemoveRegion

**FIND:**
```csharp
        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            // Clean up bots on this scene
            lock (m_bots)
```

**REPLACE WITH:**
```csharp
        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            // Save persistent bot state before cleanup
            m_persistence?.OnRegionShutdown();
            // Clean up bots on this scene
            lock (m_bots)
```

### Change 1.6 — Notify persistence on bot removal

**FIND:**
```csharp
        public void RemoveBot(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            StopAllMovement(data);
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.DeleteNPC(botID, scene);

            lock (m_bots)
                m_bots.Remove(botID);
        }
```

**REPLACE WITH:**
```csharp
        public void RemoveBot(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            StopAllMovement(data);
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.DeleteNPC(botID, scene);

            lock (m_bots)
                m_bots.Remove(botID);

            // Notify persistence manager
            m_persistence?.OnBotRemoved(botID);
        }
```

### Change 1.7 — Add console command methods before the final closing braces

**FIND:**
```csharp
        #endregion
    }
}
```
(This is the very last `#endregion` + closing braces at the end of the file.)

**REPLACE WITH:**
```csharp
        #endregion

        #region Console Commands

        private void RegisterConsoleCommands(Scene scene)
        {
            MainConsole.Instance.Commands.AddCommand(
                "BotPersistence", true, "list persistent bots",
                "list persistent bots",
                "List all active persistent bots in this region",
                HandleListPersistentBots);

            MainConsole.Instance.Commands.AddCommand(
                "BotPersistence", true, "clear persistent bots",
                "clear persistent bots [owner_uuid]",
                "Clear all persistent bots, or only those owned by owner_uuid",
                HandleClearPersistentBots);
        }

        private void HandleListPersistentBots(string module, string[] args)
        {
            if (m_persistence == null)
            {
                MainConsole.Instance.Output("Bot persistence is not enabled.");
                return;
            }

            var bots = m_persistence.ListActiveBots();
            if (bots.Count == 0)
            {
                MainConsole.Instance.Output("No active persistent bots.");
                return;
            }

            MainConsole.Instance.Output($"Active persistent bots: {bots.Count}");
            MainConsole.Instance.Output(
                $"{"Bot ID",-38} {"Name",-25} {"Owner",-38} {"Position",-20} {"Expires"}");
            MainConsole.Instance.Output(new string('-', 140));

            foreach (var bot in bots)
            {
                string expires = bot.ExpiresAt.HasValue
                    ? bot.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm")
                    : "never";
                string pos = $"<{bot.Position.X:F0},{bot.Position.Y:F0},{bot.Position.Z:F0}>";

                MainConsole.Instance.Output(
                    $"{bot.BotID,-38} {bot.BotFirstName + " " + bot.BotLastName,-25} " +
                    $"{bot.OwnerID,-38} {pos,-20} {expires}");
            }
        }

        private void HandleClearPersistentBots(string module, string[] args)
        {
            if (m_persistence == null)
            {
                MainConsole.Instance.Output("Bot persistence is not enabled.");
                return;
            }

            if (args.Length > 3 && UUID.TryParse(args[3], out UUID ownerID))
            {
                int count = m_persistence.ClearOwnerPersistentBots(ownerID);
                MainConsole.Instance.Output($"Cleared {count} persistent bots for owner {ownerID}");
            }
            else
            {
                int count = m_persistence.ClearAllPersistentBots();
                MainConsole.Instance.Output($"Cleared {count} persistent bots");
            }
        }

        #endregion
    }
}
```

---

## File 2: ISystemAPI.cs
**Location:** `InWorldz.Phlox/Glue/ISystemAPI.cs`

### Change 2.1 — Add Phase 34 declarations

**FIND:**
```csharp
        // ── Phase 33: 5 new functions (TableIndex 662–666) ──
        LSLList llGetPayPrice();
        Quaternion llGetAgentRot();
        string llGetGroundTexture(int corner);
        int llGetVehicleFlags();
        LSLList llGetLinkGLTFOverrides(int link, int face);
    }
```

**REPLACE WITH:**
```csharp
        // ── Phase 33: 5 new functions (TableIndex 662–666) ──
        LSLList llGetPayPrice();
        Quaternion llGetAgentRot();
        string llGetGroundTexture(int corner);
        int llGetVehicleFlags();
        LSLList llGetLinkGLTFOverrides(int link, int face);

        // ── Phase 34: Bot Persistence (TableIndex 667–671) ──
        int botSetPersistent(string botID, int ttlSeconds);
        int botRemovePersistent(string botID);
        int botIsPersistent(string botID);
        string botGetPersistentData(string botID, string key);
        int botSetPersistentData(string botID, string key, string value);
    }
```

---

## File 3: SyscallShim.cs
**Location:** `InWorldz.Phlox/Glue/SyscallShim.cs`

### Change 3.1 — Add table entries to dispatch array

**FIND:**
```csharp
								Shim_llGetVehicleFlags,         //665
								Shim_llGetLinkGLTFOverrides,    //666
        };
```

**REPLACE WITH:**
```csharp
								Shim_llGetVehicleFlags,         //665
								Shim_llGetLinkGLTFOverrides,    //666
								Shim_botSetPersistent,          //667
								Shim_botRemovePersistent,       //668
								Shim_botIsPersistent,           //669
								Shim_botGetPersistentData,      //670
								Shim_botSetPersistentData,      //671
        };
```

### Change 3.2 — Add shim methods at end of class

**FIND:**
```csharp
		static private void Shim_llGetLinkGLTFOverrides(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetLinkGLTFOverrides(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
    }
}
```

**REPLACE WITH:**
```csharp
		static private void Shim_llGetLinkGLTFOverrides(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetLinkGLTFOverrides(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        // ── Phase 34: Bot Persistence shim methods (667–671) ──

        static private void Shim_botSetPersistent(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botSetPersistent(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botRemovePersistent(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botRemovePersistent(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botIsPersistent(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botIsPersistent(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botGetPersistentData(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.botGetPersistentData(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botSetPersistentData(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botSetPersistentData(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }
    }
}
```

---

## File 4: LSLSystemAPI.cs
**Location:** `Phlox.ScriptEngine/LSLSystemAPI.cs`

### Change 4.1 — Add using statement

**FIND:**
```csharp
using OpenSim.Region.PhysicsModules.SharedBase;

namespace Phlox.ScriptEngine
```

**REPLACE WITH:**
```csharp
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.OptionalModules.World.NPC;

namespace Phlox.ScriptEngine
```

### Change 4.2 — Add persistence field and helper

**FIND:**
```csharp
        private IBotManager GetBotManager()
        {
            return World.RequestModuleInterface<IBotManager>();
        }
```

**REPLACE WITH:**
```csharp
        private IBotManager GetBotManager()
        {
            return World.RequestModuleInterface<IBotManager>();
        }

        private BotPersistenceManager _botPersistence;
        private BotPersistenceManager GetBotPersistence()
        {
            if (_botPersistence == null)
            {
                IBotManager mgr = GetBotManager();
                if (mgr is BotManager bm)
                    _botPersistence = bm.PersistenceManager;
            }
            return _botPersistence;
        }
```

### Change 4.3 — Add Phase 34 functions at end of class

**FIND:**
```csharp
            return new LSLList(result.ToArray());
        }
    }
}
```
(This is the end of `llGetLinkGLTFOverrides` plus the closing braces of class and namespace.)

**REPLACE WITH:**
```csharp
            return new LSLList(result.ToArray());
        }

        // ── Phase 34: Bot Persistence Functions (667–671) ──

        // ── 667: botSetPersistent ──
        public int botSetPersistent(string botID, int ttlSeconds)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.SetPersistent(id, m_host.OwnerID, m_itemID,
                m_host.ParentGroup.UUID, ttlSeconds);
        }

        // ── 668: botRemovePersistent ──
        public int botRemovePersistent(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.RemovePersistent(id, m_host.OwnerID);
        }

        // ── 669: botIsPersistent ──
        public int botIsPersistent(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return 0;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return 0;

            return pm.IsPersistent(id) ? 1 : 0;
        }

        // ── 670: botGetPersistentData ──
        public string botGetPersistentData(string botID, string key)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return string.Empty;

            return pm.GetPersistentData(id, key);
        }

        // ── 671: botSetPersistentData ──
        public int botSetPersistentData(string botID, string key, string value)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.SetPersistentData(id, m_host.OwnerID, key, value);
        }
    }
}
```

---

## File 5: BotPersistenceManager.cs (NEW FILE)
**Location:** Place in `Region/OptionalModules/World/NPC/BotPersistenceManager.cs`

This is the complete new file already provided in the outputs — no changes needed.

---

## File 6: OpenSim.ini
**Location:** `bin/OpenSim.ini`

**ADD** this new section (anywhere, but logically after `[NPC]`):

```ini
[BotPersistence]
    ;; Master switch — grid operators must opt in
    ;; When false, all persistence features are completely disabled
    Enabled = false

    ;; Database file path (relative to bin/)
    DatabaseFile = "botpersistence.db"

    ;; Capacity limits
    MaxPerParcel = 10
    MaxPerOwner = 15
    MaxPerRegion = 50

    ;; Whether persistent bots count against the region's MaxAgents limit
    ;; false = shared pool (bots reduce avatar capacity)
    ;; true = separate pool (bots use their own MaxPerRegion cap)
    SeparateAgentPool = false

    ;; Default time-to-live in seconds (0 = no expiry)
    ;; Scripts can override per-bot
    DefaultTTL = 604800  ;; 7 days

    ;; How often to save bot positions to SQLite (seconds)
    PositionSaveInterval = 60

    ;; How often to run the expiration/orphan cleanup (seconds)
    CleanupInterval = 21600  ;; 6 hours

    ;; Staggered spawning: how many bots to respawn per second on region load
    ;; Lower values = less startup impact, slower full population
    RespawnRate = 2
```

---

## NuGet Dependency

Run in the project directory that contains BotPersistenceManager.cs:

```bash
dotnet add package Microsoft.Data.Sqlite
```

---

## Build and Deploy

```bash
cd /d/legion-grid-source && dotnet build -c Release OpenSim.sln 2>&1 | tail -5

cp /d/legion-grid-source/bin/Phlox.ScriptEngine.dll "/d/opensim - Use this december 2025/bin/"
cp /d/legion-grid-source/bin/InWorldz.Phlox.dll "/d/opensim - Use this december 2025/bin/"
```

Note: If BotPersistenceManager is in a separate assembly (e.g., OpenSim.Region.OptionalModules), copy that DLL too.

---

## Summary of All Changes

| File | Changes |
|------|---------|
| BotManager.cs | 7 modifications: using, fields, Initialise, RegionLoaded, RemoveRegion, RemoveBot, console commands |
| ISystemAPI.cs | 5 new interface declarations (667–671) |
| SyscallShim.cs | 5 table entries + 5 shim methods (667–671) |
| LSLSystemAPI.cs | 1 using, 1 field+helper, 5 new functions (667–671) |
| BotPersistenceManager.cs | New file — complete persistence manager |
| OpenSim.ini | New [BotPersistence] section |
