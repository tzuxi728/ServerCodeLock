using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ServerCodeLock", "tzuxi728", "1.2.1")]
    [Description("One-time wipe PIN gate for private servers. Oxide and Carbon. Author: tzuxi728 | Telegram: @tzuxi")]
    public class ServerCodeLock : RustPlugin
    {
        #region Fields

        private const string UiRoot = "SCL.Root";
        private const string UiBlur = "SCL.Blur";
        private const string UiPanel = "SCL.Panel";
        private const string UiTitle = "SCL.Title";
        private const string UiWell = "SCL.Well";
        private const string UiDots = "SCL.Dots";
        private const string UiLed = "SCL.Led";
        private const string UiFlash = "SCL.Flash";

        private const string CmdPress = "scl.press";
        private const string CmdClear = "scl.clear";
        private const string CmdEnter = "scl.enter";

        private const string PermBypass = "servercodelock.bypass";
        private const string PermAdmin = "servercodelock.admin";

        private const string DataFile = "ServerCodeLock/state";

        private const string FxPress = "assets/prefabs/locks/keypad/effects/lock.code.lock.prefab";
        private const string FxSuccess = "assets/prefabs/locks/keypad/effects/lock.code.unlock.prefab";
        private const string FxDenied = "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab";
        private const string FxShock = "assets/prefabs/locks/keypad/effects/lock.code.shock.prefab";

        private const float WatchdogInterval = 0.75f;
        private const float SnapSqr = 1f;
        private const float FlushDelay = 2.5f;
        private const float RateWindow = 0.12f;
        private const int LogFlushAt = 24;
        private const int PasswordMaxLen = 8;
        private const int PasswordWarnLen = 4;

        private static readonly string[] DigitLabels = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "C", "0", "↵" };

        private static readonly string[] WeakPins =
        {
            "1234", "0123", "0000", "1111", "2222", "3333", "4444",
            "5555", "6666", "7777", "8888", "9999", "123456", "12345678"
        };

        private static readonly string[] LockHooks =
        {
            "OnPlayerInput",
            "OnEntityTakeDamage",
            "CanBeTargeted",
            "OnNpcTarget",
            "CanBradleyApcTarget",
            "OnTurretTarget",
            "CanHelicopterTarget",
            "OnPlayerChat",
            "OnPlayerCommand",
            "OnServerCommand",
            "OnPlayerVoice",
            "CanLootEntity",
            "CanLootPlayer",
            "CanBuild",
            "CanCraft",
            "CanPickupEntity",
            "CanSpectateTarget",
            "OnPlayerViolation",
            "OnRunPlayerMetabolism"
        };

        private PluginConfig _config;
        private StoredData _data;

        private readonly HashSet<string> _authorized = new HashSet<string>();
        private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>();
        private readonly Dictionary<string, Session> _pending = new Dictionary<string, Session>();
        private readonly HashSet<string> _busy = new HashSet<string>();
        private readonly List<string> _pendingKeys = new List<string>(16);
        private readonly List<string> _logBuf = new List<string>(32);

        private bool _dirty;
        private bool _flushQueued;
        private bool _lockHooksOn;
        private string _logDir;
        private string _wipeId = string.Empty;
        private Timer _flushTimer;
        private Timer _watchdogTimer;
        private int _gateShown;
        private int _gateSkipped;

        #endregion

        #region Types

        private class PluginConfig
        {
            [JsonProperty("Password")]
            public string Password = "1234";

            [JsonProperty("MaxAttemptsBeforeKick")]
            public int MaxAttemptsBeforeKick = 3;

            [JsonProperty("MaxAttemptsBeforeBan")]
            public int MaxAttemptsBeforeBan = 10;

            [JsonProperty("KickReason")]
            public string KickReason = "Wrong password. Too many attempts.";

            [JsonProperty("BanReason")]
            public string BanReason = "Too many failed password attempts.";

            [JsonProperty("GateTimeoutSeconds (0 = off)")]
            public int GateTimeoutSeconds = 180;

            [JsonProperty("GateTimeoutReason")]
            public string GateTimeoutReason = "Password entry timed out.";

            [JsonProperty("BypassAdmins")]
            public bool BypassAdmins = true;

            [JsonProperty("BypassModerators")]
            public bool BypassModerators = false;

            [JsonProperty("BroadcastUnlock")]
            public bool BroadcastUnlock = false;

            [JsonProperty("UseBlur")]
            public bool UseBlur = true;

            [JsonProperty("Language (en/ru)")]
            public string Language = "en";

            [JsonProperty("LogToFile")]
            public bool LogToFile = true;

            [JsonProperty("DebugVerbose")]
            public bool DebugVerbose = false;
        }

        private class StoredData
        {
            [JsonProperty("WipeId")]
            public string WipeId = string.Empty;

            [JsonProperty("Authorized")]
            public List<string> Authorized = new List<string>();

            [JsonProperty("Attempts")]
            public Dictionary<string, int> Attempts = new Dictionary<string, int>();
        }

        private class Session
        {
            public string Buffer = string.Empty;
            public Vector3 Origin;
            public float LastPress;
            public float StartedAt;
            public bool ErrorFlash;
        }

        #endregion

        #region Oxide lifecycle

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
            }
            catch (Exception ex)
            {
                ReportError("LoadConfig", ex);
                _config = new PluginConfig();
            }

            SanitizeConfig();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["UI.Title"] = "SERVER LOCK",
                ["Chat.Unlocked"] = "Code accepted. Welcome.",
                ["Chat.Broadcast"] = "{0} entered the server code.",
                ["Console.NoPerm"] = "no permission",
                ["Console.Granted"] = "granted {0}",
                ["Console.Revoked"] = "revoked {0}",
                ["Console.AlreadyAuth"] = "already authorized: {0}",
                ["Console.NotAuth"] = "not authorized: {0}",
                ["Console.BadId"] = "invalid steamid",
                ["Console.PassSet"] = "password updated, length={0}",
                ["Console.AuthReset"] = "authorization list cleared ({0})",
                ["Console.UsageGrant"] = "usage: scl.grant <steamid>",
                ["Console.UsageRevoke"] = "usage: scl.revoke <steamid>",
                ["Console.UsagePass"] = "usage: scl.setpass <digits>"
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["UI.Title"] = "КОД СЕРВЕРА",
                ["Chat.Unlocked"] = "Код принят. Добро пожаловать.",
                ["Chat.Broadcast"] = "{0} ввёл(а) код сервера.",
                ["Console.NoPerm"] = "нет прав",
                ["Console.Granted"] = "выдан доступ {0}",
                ["Console.Revoked"] = "доступ снят {0}",
                ["Console.AlreadyAuth"] = "уже в списке: {0}",
                ["Console.NotAuth"] = "нет в списке: {0}",
                ["Console.BadId"] = "некорректный steamid",
                ["Console.PassSet"] = "пароль обновлён, длина={0}",
                ["Console.AuthReset"] = "список допуска очищен ({0})",
                ["Console.UsageGrant"] = "использование: scl.grant <steamid>",
                ["Console.UsageRevoke"] = "использование: scl.revoke <steamid>",
                ["Console.UsagePass"] = "использование: scl.setpass <цифры>"
            }, this, "ru");
        }

        private void Init()
        {
            try
            {
                permission.RegisterPermission(PermBypass, this);
                permission.RegisterPermission(PermAdmin, this);

                if (_config != null && _config.LogToFile)
                {
                    try
                    {
                        _logDir = Path.Combine(Interface.Oxide.LogDirectory, "ServerCodeLock");
                        Directory.CreateDirectory(_logDir);
                    }
                    catch (Exception ex)
                    {
                        ReportError("Init.LogDir", ex);
                        _logDir = null;
                    }
                }

                LoadData();
                DisableLockHooks();

                WriteLog("INIT  1.2.1  pass_len=" + PasswordLen()
                    + " kick=" + (_config != null ? _config.MaxAttemptsBeforeKick : -1)
                    + " ban=" + (_config != null ? _config.MaxAttemptsBeforeBan : -1)
                    + " authorized=" + _authorized.Count
                    + " timeout=" + (_config != null ? _config.GateTimeoutSeconds : 0));
            }
            catch (Exception ex)
            {
                ReportError("Init", ex);
            }
        }

        private void OnServerInitialized()
        {
            try
            {
                if (_pending.Count == 0)
                    DisableLockHooks();

                ReconcileWipe("boot");

                int n = 0;
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player == null) continue;
                    n++;
                    QueueAuth(player, 0.25f);
                }
                WriteLog("BOOT  players=" + n + " wipe=" + _wipeId);
            }
            catch (Exception ex)
            {
                ReportError("OnServerInitialized", ex);
            }
        }

        private void OnNewSave(string filename)
        {
            try
            {
                ApplyWipe(BuildWipeId(), "OnNewSave:" + (filename ?? string.Empty));
            }
            catch (Exception ex)
            {
                ReportError("OnNewSave", ex);
            }
        }

        private void Unload()
        {
            try
            {
                WriteLog("UNLOAD  shown=" + _gateShown + " skipped=" + _gateSkipped
                    + " pending=" + _pending.Count + " authorized=" + _authorized.Count);
                FlushNow();
                DestroyTimer(ref _flushTimer);
                DestroyTimer(ref _watchdogTimer);

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player == null) continue;
                    DestroyUi(player);
                    if (IsPending(Sid(player)))
                        ResumeAntiCheat(player);
                }

                _pending.Clear();
                _busy.Clear();
                DisableLockHooks();
            }
            catch (Exception ex)
            {
                ReportError("Unload", ex);
            }
        }

        private static void DestroyTimer(ref Timer t)
        {
            if (t == null) return;
            if (!t.Destroyed) t.Destroy();
            t = null;
        }

        #endregion

        #region Config / data / wipe

        private void SanitizeConfig()
        {
            if (_config == null) _config = new PluginConfig();
            _config.Password = NormalizePin(_config.Password, true);

            if (_config.MaxAttemptsBeforeKick < 1) _config.MaxAttemptsBeforeKick = 3;
            if (_config.MaxAttemptsBeforeBan < _config.MaxAttemptsBeforeKick)
                _config.MaxAttemptsBeforeBan = Math.Max(10, _config.MaxAttemptsBeforeKick);
            if (_config.GateTimeoutSeconds < 0) _config.GateTimeoutSeconds = 0;

            if (string.IsNullOrEmpty(_config.KickReason))
                _config.KickReason = "Wrong password. Too many attempts.";
            if (string.IsNullOrEmpty(_config.BanReason))
                _config.BanReason = "Too many failed password attempts.";
            if (string.IsNullOrEmpty(_config.GateTimeoutReason))
                _config.GateTimeoutReason = "Password entry timed out.";

            if (string.IsNullOrEmpty(_config.Language) ||
                (!_config.Language.Equals("en", StringComparison.OrdinalIgnoreCase) &&
                 !_config.Language.Equals("ru", StringComparison.OrdinalIgnoreCase)))
            {
                _config.Language = "en";
            }

            if (IsWeakPin(_config.Password))
            {
                PrintWarning("[SCL] Password is weak or default. Change it in config or run: scl.setpass <digits>");
            }

            if (_config.MaxAttemptsBeforeBan > 20)
            {
                PrintWarning("[SCL] MaxAttemptsBeforeBan=" + _config.MaxAttemptsBeforeBan + " is wide for a private wipe lock.");
            }
        }

        private string NormalizePin(string raw, bool fallbackDefault)
        {
            if (raw == null) raw = string.Empty;
            StringBuilder digits = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c >= '0' && c <= '9') digits.Append(c);
            }

            if (digits.Length == 0)
            {
                if (fallbackDefault)
                {
                    PrintWarning("[SCL] Password must be numeric. Falling back to 1234.");
                    return "1234";
                }
                return string.Empty;
            }

            if (digits.Length > PasswordMaxLen)
            {
                PrintWarning("[SCL] Password truncated to " + PasswordMaxLen + " digits.");
                digits.Length = PasswordMaxLen;
            }
            return digits.ToString();
        }

        private static bool IsWeakPin(string pin)
        {
            if (string.IsNullOrEmpty(pin) || pin.Length < PasswordWarnLen) return true;
            for (int i = 0; i < WeakPins.Length; i++)
            {
                if (pin == WeakPins[i]) return true;
            }
            return false;
        }

        private int PasswordLen()
        {
            return _config != null && _config.Password != null ? _config.Password.Length : 0;
        }

        private void LoadData()
        {
            _authorized.Clear();
            _attempts.Clear();
            _data = null;

            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile);
            }
            catch (Exception ex)
            {
                ReportError("LoadData", ex);
            }

            if (_data == null) _data = new StoredData();
            if (_data.Authorized == null) _data.Authorized = new List<string>();
            if (_data.Attempts == null) _data.Attempts = new Dictionary<string, int>();

            for (int i = 0; i < _data.Authorized.Count; i++)
            {
                string id = _data.Authorized[i];
                if (!string.IsNullOrEmpty(id)) _authorized.Add(id);
            }

            foreach (KeyValuePair<string, int> kv in _data.Attempts)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                    _attempts[kv.Key] = kv.Value;
            }

            _wipeId = _data.WipeId ?? string.Empty;
            _dirty = false;

            if (string.IsNullOrEmpty(_wipeId) && _authorized.Count == 0 && _attempts.Count == 0)
                TryMigrateLegacy();
        }

        private void TryMigrateLegacy()
        {
            try
            {
                List<string> list = Interface.Oxide.DataFileSystem.ReadObject<List<string>>("ServerCodeLock/authorized");
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(list[i]))
                            _authorized.Add(list[i]);
                    }
                }
            }
            catch { }

            try
            {
                Dictionary<string, int> map = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, int>>("ServerCodeLock/attempts");
                if (map != null)
                {
                    foreach (KeyValuePair<string, int> kv in map)
                    {
                        if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0)
                            _attempts[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }

            if (_authorized.Count > 0 || _attempts.Count > 0)
            {
                WriteLog("MIGRATE legacy data auth=" + _authorized.Count + " attempts=" + _attempts.Count);
                _dirty = true;
            }
        }

        private void SaveData()
        {
            if (_data == null) _data = new StoredData();
            _data.WipeId = _wipeId ?? string.Empty;

            List<string> list = new List<string>(_authorized.Count);
            foreach (string id in _authorized) list.Add(id);
            list.Sort(StringComparer.Ordinal);
            _data.Authorized = list;
            _data.Attempts = new Dictionary<string, int>(_attempts);

            Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
        }

        private void QueueSave()
        {
            _dirty = true;
            if (_flushQueued) return;
            _flushQueued = true;
            _flushTimer = timer.Once(FlushDelay, FlushNow);
        }

        private void FlushNow()
        {
            _flushQueued = false;
            DestroyTimer(ref _flushTimer);

            if (_dirty)
            {
                _dirty = false;
                try { SaveData(); }
                catch (Exception ex)
                {
                    ReportError("SaveData", ex);
                    _dirty = true;
                }
            }

            FlushLog();
        }

        private void ReconcileWipe(string reason)
        {
            string current = BuildWipeId();
            if (string.IsNullOrEmpty(_wipeId))
            {
                _wipeId = current;
                QueueSave();
                return;
            }

            if (string.Equals(_wipeId, current, StringComparison.Ordinal))
                return;

            ApplyWipe(current, reason);
        }

        private void ApplyWipe(string newId, string reason)
        {
            int prevAuth = _authorized.Count;
            int prevAtt = _attempts.Count;

            _wipeId = newId ?? string.Empty;
            _authorized.Clear();
            _attempts.Clear();
            _busy.Clear();

            List<string> ids = CopyPendingKeys();
            for (int i = 0; i < ids.Count; i++)
            {
                BasePlayer p = FindOnline(ids[i]);
                if (p != null)
                {
                    try { DestroyUi(p); } catch { }
                    ResumeAntiCheat(p);
                }
            }
            _pending.Clear();
            StopWatchdog();
            DisableLockHooks();

            _dirty = true;
            FlushNow();
            WriteLog("WIPE   reason=" + reason + " id=" + _wipeId
                + " cleared_auth=" + prevAuth + " cleared_attempts=" + prevAtt);

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                QueueAuth(player, 0.2f);
            }
        }

        private static string BuildWipeId()
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                    World.Seed, World.Size, SaveRestore.SaveCreatedTime.Ticks);
            }
            catch
            {
                try { return World.SaveFileName ?? "unknown"; }
                catch { return "unknown"; }
            }
        }

        #endregion

        #region Auth flow

        private static string Sid(BasePlayer player)
        {
            if (player == null) return string.Empty;
            try
            {
                string id = player.UserIDString;
                return id ?? string.Empty;
            }
            catch
            {
                try { return player.userID.ToString(); }
                catch { return string.Empty; }
            }
        }

        private bool IsAuthorized(string id)
        {
            return !string.IsNullOrEmpty(id) && _authorized.Contains(id);
        }

        private bool IsPending(string id)
        {
            return !string.IsNullOrEmpty(id) && _pending.ContainsKey(id);
        }

        private bool HasStaffBypass(BasePlayer player)
        {
            if (player == null) return false;

            string id = Sid(player);
            if (string.IsNullOrEmpty(id)) return false;

            try
            {
                if (permission.UserHasPermission(id, PermBypass))
                    return true;
            }
            catch (Exception ex)
            {
                ReportError("HasStaffBypass.perm", ex);
            }

            if (_config != null && _config.BypassAdmins)
            {
                try { if (player.IsAdmin) return true; }
                catch { }
            }

            if (_config != null && _config.BypassModerators)
            {
                try
                {
                    if (player.net != null && player.net.connection != null && player.net.connection.authLevel >= 1)
                        return true;
                }
                catch { }
            }

            return false;
        }

        private void QueueAuth(BasePlayer player, float delay)
        {
            if (player == null) return;

            string id = Sid(player);
            string name = SafeName(player);

            if (IsAuthorized(id))
            {
                _gateSkipped++;
                Dbg("SKIP " + id + " authorized");
                return;
            }

            if (HasStaffBypass(player))
            {
                _gateSkipped++;
                WriteLog("SKIP  " + id + "  " + name + "  bypass");
                return;
            }

            if (delay <= 0f)
            {
                BeginAuth(player);
                return;
            }

            timer.Once(delay, () =>
            {
                if (player == null || !player.IsConnected) return;
                if (IsAuthorized(Sid(player)) || HasStaffBypass(player))
                {
                    _gateSkipped++;
                    return;
                }
                if (player.IsReceivingSnapshot)
                {
                    QueueAuth(player, 0.4f);
                    return;
                }
                BeginAuth(player);
            });
        }

        private void BeginAuth(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            string id = Sid(player);
            if (string.IsNullOrEmpty(id)) return;
            if (IsAuthorized(id) || HasStaffBypass(player)) return;

            Session session;
            if (!_pending.TryGetValue(id, out session))
            {
                session = new Session();
                _pending[id] = session;
            }

            session.Origin = player.transform.position;
            session.Buffer = string.Empty;
            session.ErrorFlash = false;
            session.StartedAt = Time.realtimeSinceStartup;
            session.LastPress = 0f;

            PauseAntiCheat(player);
            DrawUi(player, session);
            EnableLockHooks();
            StartWatchdog();

            _gateShown++;
            WriteLog("GATE  " + id + "  " + SafeName(player)
                + "  pending=" + _pending.Count
                + "  attempts=" + GetAttempts(id));
        }

        private void ReleaseGate(BasePlayer player)
        {
            if (player == null) return;
            string id = Sid(player);
            _pending.Remove(id);
            _busy.Remove(id);
            try { DestroyUi(player); } catch (Exception ex) { ReportError("ReleaseGate.UI", ex); }
            ResumeAntiCheat(player);

            if (_pending.Count == 0)
            {
                StopWatchdog();
                DisableLockHooks();
            }
        }

        private void Succeed(BasePlayer player, Session session)
        {
            string id = Sid(player);
            PlayFx(player, FxSuccess);

            _authorized.Add(id);
            _attempts.Remove(id);
            QueueSave();

            ReleaseGate(player);
            WriteLog("OK    " + id + "  " + SafeName(player));
            NotifyUnlock(player);
            FlushNow();
        }

        private void NotifyUnlock(BasePlayer player)
        {
            try
            {
                PrintToChat(player, "{0}", L("Chat.Unlocked", player));
                if (_config != null && _config.BroadcastUnlock)
                {
                    PrintToChat("{0}", string.Format(L("Chat.Broadcast", player), SafeName(player)));
                }
            }
            catch (Exception ex)
            {
                ReportError("NotifyUnlock", ex);
            }
        }

        private void Fail(BasePlayer player, Session session, int enteredLen)
        {
            string id = Sid(player);
            int n;
            if (!_attempts.TryGetValue(id, out n)) n = 0;
            n++;
            _attempts[id] = n;
            QueueSave();

            session.Buffer = string.Empty;
            session.ErrorFlash = true;

            PlayFx(player, FxDenied);
            PlayFx(player, FxShock);
            WriteLog("FAIL  " + id + "  " + SafeName(player) + "  len=" + enteredLen + "  n=" + n);

            if (n >= _config.MaxAttemptsBeforeBan)
            {
                WriteLog("BAN   " + id + "  " + SafeName(player) + "  n=" + n);
                ReleaseGate(player);
                FlushNow();
                BanPlayer(player, _config.BanReason);
                return;
            }

            if (n >= _config.MaxAttemptsBeforeKick)
            {
                WriteLog("KICK  " + id + "  " + SafeName(player) + "  n=" + n);
                ReleaseGate(player);
                FlushNow();
                try { player.Kick(_config.KickReason); } catch (Exception ex) { ReportError("Fail.Kick", ex); }
                return;
            }

            UpdateDisplay(player, session, true);
            timer.Once(0.35f, () =>
            {
                if (player == null || !player.IsConnected) return;
                Session s;
                if (!_pending.TryGetValue(Sid(player), out s)) return;
                s.ErrorFlash = false;
                UpdateDisplay(player, s, false);
            });
        }

        private int GetAttempts(string id)
        {
            int n;
            return _attempts.TryGetValue(id, out n) ? n : 0;
        }

        #endregion

        #region Anti-cheat / watchdog

        private void PauseAntiCheat(BasePlayer player)
        {
            if (player == null) return;
            try
            {
                player.PauseFlyHackDetection(300f);
                player.PauseSpeedHackDetection(300f);
            }
            catch (Exception ex)
            {
                ReportError("PauseAntiCheat", ex);
            }
        }

        private void ResumeAntiCheat(BasePlayer player)
        {
            if (player == null) return;
            try
            {
                player.PauseFlyHackDetection(0.1f);
                player.PauseSpeedHackDetection(0.1f);
            }
            catch { }
        }

        private void SnapBack(BasePlayer player, Session session)
        {
            if (player == null || player.IsDestroyed) return;
            Vector3 pos = player.transform.position;
            if ((pos - session.Origin).sqrMagnitude < SnapSqr) return;
            try
            {
                player.MovePosition(session.Origin);
                try { player.SendNetworkUpdateImmediate(); } catch { }
            }
            catch (Exception ex)
            {
                ReportError("SnapBack", ex);
            }
        }

        private void StartWatchdog()
        {
            if (_watchdogTimer != null && !_watchdogTimer.Destroyed) return;
            _watchdogTimer = timer.Every(WatchdogInterval, Watchdog);
        }

        private void StopWatchdog()
        {
            DestroyTimer(ref _watchdogTimer);
        }

        private void Watchdog()
        {
            if (_pending.Count == 0)
            {
                StopWatchdog();
                DisableLockHooks();
                return;
            }

            float now = Time.realtimeSinceStartup;
            int timeout = _config != null ? _config.GateTimeoutSeconds : 0;
            List<string> keys = CopyPendingKeys();

            for (int i = 0; i < keys.Count; i++)
            {
                string id = keys[i];
                Session session;
                if (!_pending.TryGetValue(id, out session)) continue;

                BasePlayer player = FindOnline(id);
                if (player == null || !player.IsConnected)
                {
                    _pending.Remove(id);
                    _busy.Remove(id);
                    continue;
                }

                if (timeout > 0 && now - session.StartedAt >= timeout)
                {
                    WriteLog("TIME  " + id + "  " + SafeName(player));
                    ReleaseGate(player);
                    try { player.Kick(_config.GateTimeoutReason); } catch { }
                    continue;
                }

                SnapBack(player, session);
            }

            if (_pending.Count == 0)
            {
                StopWatchdog();
                DisableLockHooks();
            }
        }

        private List<string> CopyPendingKeys()
        {
            _pendingKeys.Clear();
            foreach (KeyValuePair<string, Session> kv in _pending)
                _pendingKeys.Add(kv.Key);
            return _pendingKeys;
        }

        private static BasePlayer FindOnline(string id)
        {
            ulong uid;
            if (!ulong.TryParse(id, out uid)) return null;
            return BasePlayer.FindByID(uid);
        }

        #endregion

        #region Input

        private void PressDigit(BasePlayer player, char digit)
        {
            if (player == null) return;
            string id = Sid(player);
            Session session;
            if (!_pending.TryGetValue(id, out session)) return;
            if (!RateOk(session)) return;
            if (_busy.Contains(id)) return;

            int cap = PasswordLen();
            if (cap < 1 || session.Buffer.Length >= cap) return;

            session.Buffer += digit;
            PlayFx(player, FxPress);
            UpdateDots(player, session);

            if (session.Buffer.Length >= cap)
                Submit(player, session);
        }

        private void ClearBuffer(BasePlayer player)
        {
            if (player == null) return;
            string id = Sid(player);
            Session session;
            if (!_pending.TryGetValue(id, out session)) return;
            if (!RateOk(session)) return;
            if (session.Buffer.Length == 0) return;
            session.Buffer = string.Empty;
            PlayFx(player, FxPress);
            UpdateDots(player, session);
        }

        private void Submit(BasePlayer player, Session session)
        {
            if (player == null || session == null) return;
            string id = Sid(player);
            if (session.Buffer.Length != PasswordLen()) return;
            if (!_busy.Add(id)) return;

            try
            {
                string entered = session.Buffer;
                if (entered == _config.Password)
                    Succeed(player, session);
                else
                    Fail(player, session, entered.Length);
            }
            catch (Exception ex)
            {
                ReportError("Submit", ex);
            }
            finally
            {
                _busy.Remove(id);
            }
        }

        private bool RateOk(Session session)
        {
            float now = Time.realtimeSinceStartup;
            if (now - session.LastPress < RateWindow) return false;
            session.LastPress = now;
            return true;
        }

        [ConsoleCommand("scl.press")]
        private void CmdPressDigit(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg != null ? arg.Player() : null;
            if (player == null || arg == null || !arg.HasArgs(1)) return;
            string s = ArgString(arg, 0);
            if (string.IsNullOrEmpty(s) || s.Length != 1 || s[0] < '0' || s[0] > '9') return;
            PressDigit(player, s[0]);
        }

        [ConsoleCommand("scl.clear")]
        private void CmdClearBuffer(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg != null ? arg.Player() : null;
            if (player == null) return;
            ClearBuffer(player);
        }

        [ConsoleCommand("scl.enter")]
        private void CmdSubmit(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg != null ? arg.Player() : null;
            if (player == null) return;
            Session session;
            if (!_pending.TryGetValue(Sid(player), out session)) return;
            if (!RateOk(session)) return;
            Submit(player, session);
        }

        [ConsoleCommand("scl.grant")]
        private void CmdGrant(ConsoleSystem.Arg arg)
        {
            if (!CanAdmin(arg)) { Reply(arg, L("Console.NoPerm", arg)); return; }
            if (arg == null || !arg.HasArgs(1)) { Reply(arg, L("Console.UsageGrant", arg)); return; }

            string id = NormalizeSteamId(ArgString(arg, 0));
            if (id == null) { Reply(arg, L("Console.BadId", arg)); return; }

            if (!_authorized.Add(id))
            {
                Reply(arg, string.Format(L("Console.AlreadyAuth", arg), id));
                return;
            }

            QueueSave();
            WriteLog("GRANT " + id + "  by " + AdminTag(arg));
            FlushNow();

            BasePlayer target = FindOnline(id);
            if (target != null && target.IsConnected && IsPending(id))
                ReleaseGate(target);

            Reply(arg, string.Format(L("Console.Granted", arg), id));
        }

        [ConsoleCommand("scl.revoke")]
        private void CmdRevoke(ConsoleSystem.Arg arg)
        {
            if (!CanAdmin(arg)) { Reply(arg, L("Console.NoPerm", arg)); return; }
            if (arg == null || !arg.HasArgs(1)) { Reply(arg, L("Console.UsageRevoke", arg)); return; }

            string id = NormalizeSteamId(ArgString(arg, 0));
            if (id == null) { Reply(arg, L("Console.BadId", arg)); return; }

            if (!_authorized.Remove(id))
            {
                Reply(arg, string.Format(L("Console.NotAuth", arg), id));
                return;
            }

            QueueSave();
            WriteLog("REVOKE " + id + "  by " + AdminTag(arg));
            FlushNow();

            BasePlayer target = FindOnline(id);
            if (target != null && target.IsConnected && !HasStaffBypass(target))
                QueueAuth(target, 0.1f);

            Reply(arg, string.Format(L("Console.Revoked", arg), id));
        }

        [ConsoleCommand("scl.setpass")]
        private void CmdSetPass(ConsoleSystem.Arg arg)
        {
            if (!CanAdmin(arg)) { Reply(arg, L("Console.NoPerm", arg)); return; }
            if (arg == null || !arg.HasArgs(1)) { Reply(arg, L("Console.UsagePass", arg)); return; }

            string pin = NormalizePin(ArgString(arg, 0), false);
            if (string.IsNullOrEmpty(pin))
            {
                Reply(arg, L("Console.UsagePass", arg));
                return;
            }

            _config.Password = pin;
            SaveConfig();
            WriteLog("PASS  length=" + pin.Length + "  by " + AdminTag(arg) + (IsWeakPin(pin) ? "  weak" : string.Empty));

            List<string> ids = CopyPendingKeys();
            for (int i = 0; i < ids.Count; i++)
            {
                Session session;
                if (!_pending.TryGetValue(ids[i], out session)) continue;
                session.Buffer = string.Empty;
                session.ErrorFlash = false;
                BasePlayer target = FindOnline(ids[i]);
                if (target != null && target.IsConnected)
                    DrawUi(target, session);
            }

            Reply(arg, string.Format(L("Console.PassSet", arg), pin.Length));
            if (IsWeakPin(pin))
                Reply(arg, "warning: weak password");
        }

        [ConsoleCommand("scl.resetauth")]
        private void CmdResetAuth(ConsoleSystem.Arg arg)
        {
            if (!CanAdmin(arg)) { Reply(arg, L("Console.NoPerm", arg)); return; }

            int n = _authorized.Count;
            _authorized.Clear();
            _attempts.Clear();
            QueueSave();
            WriteLog("RESET auth by " + AdminTag(arg) + " cleared=" + n);
            FlushNow();

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (HasStaffBypass(player)) continue;
                if (IsPending(Sid(player))) continue;
                QueueAuth(player, 0.1f);
            }

            Reply(arg, string.Format(L("Console.AuthReset", arg), n));
        }

        [ConsoleCommand("scl.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!CanAdmin(arg)) { Reply(arg, L("Console.NoPerm", arg)); return; }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("SCL 1.2.1 auth=").Append(_authorized.Count)
                .Append(" pending=").Append(_pending.Count)
                .Append(" attempts=").Append(_attempts.Count)
                .Append(" shown=").Append(_gateShown)
                .Append(" skipped=").Append(_gateSkipped)
                .Append(" pass_len=").Append(PasswordLen())
                .Append(" hooks=").Append(_lockHooksOn ? 1 : 0)
                .Append(" wipe=").Append(_wipeId);
            string report = sb.ToString();
            Puts(report);
            Reply(arg, report);
        }

        private bool CanAdmin(ConsoleSystem.Arg arg)
        {
            if (arg == null) return false;
            if (arg.Connection == null) return true;
            BasePlayer player = arg.Player();
            if (player == null) return false;
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(Sid(player), PermAdmin);
        }

        private static string AdminTag(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection == null) return "CONSOLE";
            BasePlayer player = arg.Player();
            return player == null ? "?" : Sid(player);
        }

        private void Reply(ConsoleSystem.Arg arg, string message)
        {
            if (arg == null) return;
            try { arg.ReplyWith(message); } catch { }
        }

        private static string ArgString(ConsoleSystem.Arg arg, int index)
        {
            if (arg == null || arg.Args == null || index < 0 || index >= arg.Args.Length) return null;
            try
            {
                object v = arg.Args[index];
                if (v == null) return null;
                string s = v.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeSteamId(object raw)
        {
            if (raw == null) return null;
            string s;
            try { s = raw.ToString(); }
            catch { return null; }
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            ulong uid;
            if (!ulong.TryParse(s, out uid) || uid == 0UL) return null;
            return uid.ToString();
        }

        #endregion

        #region CUI

        private void DestroyUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRoot);
        }

        private void DrawUi(BasePlayer player, Session session)
        {
            if (player == null || !player.IsConnected) return;

            try
            {
                DestroyUi(player);

                CuiElementContainer c = new CuiElementContainer();

                c.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    CursorEnabled = true,
                    KeyboardEnabled = false
                }, "Overlay", UiRoot);

                if (_config != null && _config.UseBlur)
                {
                    c.Add(new CuiElement
                    {
                        Name = UiBlur,
                        Parent = UiRoot,
                        Components =
                        {
                            new CuiImageComponent { Material = "assets/content/ui/uibackgroundblur.mat", Color = "0.55 0.55 0.55 0.82" },
                            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                        }
                    });
                }
                else
                {
                    c.Add(new CuiPanel
                    {
                        Image = { Color = "0.02 0.02 0.02 0.72" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }, UiRoot);
                }

                c.Add(new CuiPanel
                {
                    Image = { Color = "0.20 0.19 0.17 0.98" },
                    RectTransform =
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = "-132 -206",
                        OffsetMax = "132 206"
                    }
                }, UiRoot, UiPanel);

                c.Add(new CuiPanel
                {
                    Image = { Color = "0.12 0.11 0.10 1" },
                    RectTransform = { AnchorMin = "0.045 0.045", AnchorMax = "0.955 0.955" }
                }, UiPanel, "SCL.Inner");

                c.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = L("UI.Title", player),
                        Font = "RobotoCondensed-Bold.ttf",
                        FontSize = 13,
                        Align = TextAnchor.MiddleCenter,
                        Color = "0.72 0.70 0.64 1"
                    },
                    RectTransform = { AnchorMin = "0.08 0.905", AnchorMax = "0.92 0.975" }
                }, UiPanel, UiTitle);

                AddWell(c, session);
                AddLed(c, session.ErrorFlash);

                for (int i = 0; i < 12; i++)
                {
                    int col = i % 3;
                    int row = i / 3;
                    string min, max;
                    GridCell(col, row, out min, out max);

                    string label = DigitLabels[i];
                    string cmd;
                    string btnColor = "0.30 0.29 0.26 1";
                    if (i == 9)
                    {
                        cmd = CmdClear;
                        btnColor = "0.36 0.20 0.16 1";
                    }
                    else if (i == 11)
                    {
                        cmd = CmdEnter;
                        btnColor = "0.20 0.32 0.20 1";
                    }
                    else
                    {
                        cmd = CmdPress + " " + label;
                    }

                    c.Add(new CuiButton
                    {
                        Button = { Command = cmd, Color = btnColor },
                        RectTransform = { AnchorMin = min, AnchorMax = max },
                        Text =
                        {
                            Text = label,
                            Font = "RobotoCondensed-Bold.ttf",
                            FontSize = 20,
                            Align = TextAnchor.MiddleCenter,
                            Color = "0.92 0.91 0.88 1"
                        }
                    }, UiPanel, "SCL.B" + i);
                }

                CuiHelper.AddUi(player, c);
            }
            catch (Exception ex)
            {
                ReportError("DrawUi", ex);
            }
        }

        private void AddWell(CuiElementContainer c, Session session)
        {
            string well = session.ErrorFlash ? "0.28 0.04 0.03 1" : "0.04 0.045 0.035 1";
            c.Add(new CuiPanel
            {
                Image = { Color = well },
                RectTransform = { AnchorMin = "0.08 0.78", AnchorMax = "0.80 0.89" }
            }, UiPanel, UiWell);
            AddDotsLabel(c, session);
        }

        private void AddDotsLabel(CuiElementContainer c, Session session)
        {
            string dots = BuildDots(session.Buffer, PasswordLen());
            string dotColor = session.ErrorFlash ? "0.95 0.35 0.28 1" : "0.78 0.90 0.55 1";
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = dots,
                    Font = "RobotoCondensed-Bold.ttf",
                    FontSize = 22,
                    Align = TextAnchor.MiddleCenter,
                    Color = dotColor
                },
                RectTransform = { AnchorMin = "0", AnchorMax = "1" }
            }, UiWell, UiDots);
        }

        private void AddLed(CuiElementContainer c, bool error)
        {
            string led = error ? "0.85 0.12 0.10 1" : "0.72 0.16 0.12 1";
            c.Add(new CuiPanel
            {
                Image = { Color = led },
                RectTransform = { AnchorMin = "0.84 0.795", AnchorMax = "0.925 0.875" }
            }, UiPanel, UiLed);
        }

        private void UpdateDots(BasePlayer player, Session session)
        {
            if (player == null || !player.IsConnected) return;
            try
            {
                CuiHelper.DestroyUi(player, UiDots);
                CuiElementContainer c = new CuiElementContainer();
                AddDotsLabel(c, session);
                CuiHelper.AddUi(player, c);
            }
            catch (Exception ex)
            {
                ReportError("UpdateDots", ex);
            }
        }

        private void UpdateDisplay(BasePlayer player, Session session, bool showFlash)
        {
            if (player == null || !player.IsConnected) return;
            try
            {
                CuiHelper.DestroyUi(player, UiDots);
                CuiHelper.DestroyUi(player, UiWell);
                CuiHelper.DestroyUi(player, UiLed);
                CuiHelper.DestroyUi(player, UiFlash);

                CuiElementContainer c = new CuiElementContainer();
                AddWell(c, session);
                AddLed(c, session.ErrorFlash);
                if (showFlash)
                {
                    c.Add(new CuiPanel
                    {
                        Image = { Color = "0.55 0.08 0.06 0.28" },
                        RectTransform = { AnchorMin = "0", AnchorMax = "1" }
                    }, UiPanel, UiFlash);
                }
                CuiHelper.AddUi(player, c);
            }
            catch (Exception ex)
            {
                ReportError("UpdateDisplay", ex);
            }
        }

        private static string BuildDots(string buffer, int total)
        {
            if (total < 1) total = 4;
            int filled = buffer != null ? buffer.Length : 0;
            StringBuilder sb = new StringBuilder(total * 2);
            for (int i = 0; i < total; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i < filled ? '*' : '-');
            }
            return sb.ToString();
        }

        private static void GridCell(int col, int row, out string min, out string max)
        {
            const float left = 0.08f;
            const float right = 0.92f;
            const float top = 0.74f;
            const float bottom = 0.07f;
            const int cols = 3;
            const int rows = 4;
            const float gapX = 0.035f;
            const float gapY = 0.028f;
            float cellW = (right - left - gapX * (cols - 1)) / cols;
            float cellH = (top - bottom - gapY * (rows - 1)) / rows;
            float x = left + col * (cellW + gapX);
            float y = top - (row + 1) * cellH - row * gapY;
            min = string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####}", x, y);
            max = string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1:0.####}", x + cellW, y + cellH);
        }

        #endregion

        #region Effects / log / ban

        private void PlayFx(BasePlayer player, string path)
        {
            if (player == null || player.net == null || player.net.connection == null) return;
            try
            {
                Effect effect = new Effect(path, player, 0, Vector3.zero, Vector3.forward);
                EffectNetwork.Send(effect, player.net.connection);
            }
            catch
            {
            }
        }

        private void BanPlayer(BasePlayer player, string reason)
        {
            if (player == null) return;
            try
            {
                if (player.IPlayer != null)
                {
                    player.IPlayer.Ban(reason);
                    WriteLog("BAN_OK IPlayer " + Sid(player));
                    return;
                }
            }
            catch (Exception ex)
            {
                ReportError("BanPlayer.IPlayer", ex);
            }

            try
            {
                ServerUsers.Set(player.userID, ServerUsers.UserGroup.Banned, SafeName(player), reason);
                ServerUsers.Save();
                WriteLog("BAN_OK ServerUsers " + Sid(player));
            }
            catch (Exception ex)
            {
                ReportError("BanPlayer.ServerUsers", ex);
            }

            try { player.Kick(reason); }
            catch (Exception ex) { ReportError("BanPlayer.Kick", ex); }
        }

        private static string SafeName(BasePlayer player)
        {
            if (player == null) return "-";
            string n = null;
            try { n = player.displayName; } catch { n = null; }
            if (string.IsNullOrEmpty(n)) return "-";
            n = n.Replace('\n', ' ').Replace('\r', ' ');
            if (n.Length > 32) n = n.Substring(0, 32);
            return n;
        }

        private string L(string key, BasePlayer player)
        {
            string id = player != null ? Sid(player) : null;
            return lang.GetMessage(key, this, string.IsNullOrEmpty(id) ? null : id);
        }

        private string L(string key, ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg != null ? arg.Player() : null;
            if (player != null) return L(key, player);
            return lang.GetMessage(key, this);
        }

        private void Dbg(string line)
        {
            if (_config == null || !_config.DebugVerbose) return;
            WriteLog("DBG   " + line);
        }

        private void ReportError(string where, Exception ex)
        {
            if (ex == null) return;
            string msg = "ERR   @" + where + "  " + ex.GetType().Name + ": " + ex.Message;
            PrintError("[ServerCodeLock] " + msg);
            WriteLog(msg);
            FlushLog();
        }

        private void WriteLog(string line)
        {
            Puts("[SCL] " + line);
            if (_config != null && !_config.LogToFile) return;
            _logBuf.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line);
            if (_logBuf.Count >= LogFlushAt)
                FlushLog();
        }

        private void FlushLog()
        {
            if (_logBuf.Count == 0)
                return;
            if (string.IsNullOrEmpty(_logDir))
            {
                _logBuf.Clear();
                return;
            }

            try
            {
                string file = Path.Combine(_logDir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                StringBuilder sb = new StringBuilder(_logBuf.Count * 96);
                for (int i = 0; i < _logBuf.Count; i++)
                    sb.Append(_logBuf[i]).Append(Environment.NewLine);
                File.AppendAllText(file, sb.ToString());
                _logBuf.Clear();
            }
            catch (Exception ex)
            {
                PrintError("[ServerCodeLock] Log write failed: " + ex.Message);
                _logBuf.Clear();
            }
        }

        #endregion

        #region Hook subscription

        private void EnableLockHooks()
        {
            if (_lockHooksOn) return;
            _lockHooksOn = true;
            for (int i = 0; i < LockHooks.Length; i++)
            {
                if (IsCarbon && IsCarbonBrokenHook(LockHooks[i])) continue;
                try { Subscribe(LockHooks[i]); }
                catch { }
            }
        }

        private static bool IsCarbon
        {
            get
            {
                try
                {
                    Type t = Interface.Oxide.GetType();
                    return t.Namespace != null && t.Namespace.StartsWith("Carbon");
                }
                catch { return false; }
            }
        }

        private static bool IsCarbonBrokenHook(string hook)
        {
            // Carbon: CanLootEntity fails to patch WorldItem.RPC_OpenLoot (invalid IL code).
            return hook == "CanLootEntity";
        }

        private void DisableLockHooks()
        {
            _lockHooksOn = false;
            for (int i = 0; i < LockHooks.Length; i++)
            {
                try { Unsubscribe(LockHooks[i]); }
                catch { }
            }
        }

        #endregion

        #region Hooks — always on

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            WriteLog("JOIN  " + Sid(player) + "  " + SafeName(player));
            QueueAuth(player, 0.6f);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            string id = Sid(player);
            if (IsAuthorized(id) || HasStaffBypass(player) || IsPending(id)) return;
            QueueAuth(player, 0.15f);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            string id = Sid(player);
            if (IsAuthorized(id) || HasStaffBypass(player) || IsPending(id)) return;
            QueueAuth(player, 0.15f);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            string id = Sid(player);
            bool wasPending = _pending.Remove(id);
            _busy.Remove(id);
            if (wasPending)
            {
                ResumeAntiCheat(player);
                if (_pending.Count == 0)
                {
                    StopWatchdog();
                    DisableLockHooks();
                }
                WriteLog("DISC  " + id + "  " + SafeName(player) + "  pending");
            }
        }

        #endregion

        #region Hooks — lock pack (subscribed only while someone is gated)

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null || _pending.Count == 0) return;
            Session session;
            if (!_pending.TryGetValue(Sid(player), out session)) return;
            input.current.buttons = 0;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (_pending.Count == 0) return null;
            BasePlayer victim = entity as BasePlayer;
            if (victim != null && IsPending(Sid(victim))) return true;
            if (info != null && info.InitiatorPlayer != null && IsPending(Sid(info.InitiatorPlayer))) return true;
            return null;
        }

        private object CanBeTargeted(BasePlayer player, MonoBehaviour behaviour)
        {
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object OnNpcTarget(BaseEntity entity, BaseEntity target)
        {
            BasePlayer player = target as BasePlayer;
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object CanBradleyApcTarget(BradleyAPC apc, BaseEntity entity)
        {
            BasePlayer player = entity as BasePlayer;
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object OnTurretTarget(AutoTurret turret, BaseCombatEntity entity)
        {
            BasePlayer player = entity as BasePlayer;
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object CanHelicopterTarget(PatrolHelicopterAI heli, BasePlayer player)
        {
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player != null && IsPending(Sid(player))) return true;
            return null;
        }

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || !IsPending(Sid(player))) return null;
            if (IsGateCommand(command)) return null;
            return true;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.Connection == null) return null;
            BasePlayer player = arg.Player();
            if (player == null || !IsPending(Sid(player))) return null;

            string name = string.Empty;
            try
            {
                if (arg.cmd != null && arg.cmd.FullName != null)
                    name = arg.cmd.FullName;
            }
            catch { name = string.Empty; }

            if (IsGateCommand(name)) return null;
            return true;
        }

        private object OnPlayerVoice(BasePlayer player, byte[] data)
        {
            if (player != null && IsPending(Sid(player))) return true;
            return null;
        }

        private object CanLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object CanLootPlayer(BasePlayer looter, BasePlayer target)
        {
            if (looter != null && IsPending(Sid(looter))) return false;
            if (target != null && IsPending(Sid(target))) return false;
            return null;
        }

        private object CanBuild(Planner planner, Construction prefab)
        {
            BasePlayer player = planner == null ? null : planner.GetOwnerPlayer();
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object CanCraft(ItemCrafter crafter, ItemBlueprint bp, int amount)
        {
            if (crafter == null) return null;
            BasePlayer player = crafter.baseEntity as BasePlayer;
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object CanSpectateTarget(BasePlayer player, string filter)
        {
            if (player != null && IsPending(Sid(player))) return false;
            return null;
        }

        private object OnPlayerViolation(BasePlayer player, AntiHackType type, float amount)
        {
            if (player != null && IsPending(Sid(player))) return true;
            return null;
        }

        private object OnRunPlayerMetabolism(PlayerMetabolism metabolism, BaseCombatEntity entity)
        {
            BasePlayer player = entity as BasePlayer;
            if (player == null || !IsPending(Sid(player)) || metabolism == null) return null;
            metabolism.bleeding.value = 0f;
            metabolism.poison.value = 0f;
            metabolism.radiation_poison.value = 0f;
            metabolism.oxygen.value = 1f;
            metabolism.wetness.value = 0f;
            metabolism.temperature.value = 20f;
            return true;
        }

        private static bool IsGateCommand(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("scl.press", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("scl.clear", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("scl.enter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        #endregion
    }
}
