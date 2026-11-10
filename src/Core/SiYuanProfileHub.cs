using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XiaoLv.Desktop.Core
{
    public class ProfileApplyResult
    {
        public bool Success { get; set; }
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string ProfileType { get; set; }
        public bool HasRestoredCredentials { get; set; }
        public int RestoredCredentialCount { get; set; }
        public string AppliedSummary { get; set; }

        public ProfileApplyResult()
        {
            Success = false;
            ProfileId = "";
            ProfileName = "";
            ProfileType = "";
            HasRestoredCredentials = false;
            RestoredCredentialCount = 0;
            AppliedSummary = "";
        }
    }

    public class SiYuanProfileEntry
    {
        public string Id { get; set; }
        public string BlockId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }            // "s3" | "webdav" | "ai" | "appearance" | "export"
        public string Description { get; set; }
        public Dictionary<string, object> ConfigData { get; set; }
        public string CreatedTime { get; set; }
        public string UpdatedTime { get; set; }
        public bool HasVaultCredentials { get; set; }
        public bool IsActive { get; set; }
        public string CaptureWarning { get; set; }
        public bool IsLocalOnly { get; set; }

        public SiYuanProfileEntry()
        {
            Id = Guid.NewGuid().ToString("N");
            BlockId = "";
            Name = "";
            Type = "s3";
            Description = "";
            ConfigData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            CreatedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            UpdatedTime = CreatedTime;
            HasVaultCredentials = false;
            IsActive = false;
            CaptureWarning = "";
            IsLocalOnly = false;
        }
    }

    /// <summary>
    /// 思源外骨骼 · 思源常用配置预设与极速切换中枢 (SiYuanProfileHub)
    /// 1. 深度支持 S3 对象存储、WebDAV、AI 驱动源、外观与导出预设的解耦保存与秒级调用
    /// 2. 严格凭据脱敏安全铁律：自动剥离 AccessKey、SecretKey、ApiKey 与 Password，杜绝密钥外泄
    /// 3. 本地脱机缓存 (siyuan_profiles.json) ✕ 思源原生文档《/配置/预设中枢》双轨自愈
    /// 4. 内置主流云厂商（Cloudflare R2、阿里OSS、腾讯COS、MinIO）与主流大模型（DeepSeek、通义千问、智谱GLM、本地Ollama）常用标准模板
    /// </summary>
    public static class SiYuanProfileHub
    {
        private static readonly object _lock = new object();
        private static readonly SemaphoreSlim _mutationGate = new SemaphoreSlim(1, 1);
        private static readonly HashSet<string> _deletedProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public const string TARGET_DOC_PATH = "/配置预设";

        private static string LocalCachePath
        {
            get
            {
                string dir = EmbeddedResourceManager.AppDataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "siyuan_profiles.json");
            }
        }

        private static string DpapiVaultPath
        {
            get
            {
                string dir = EmbeddedResourceManager.AppDataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "local_vault_dpapi.json");
            }
        }

        private static readonly Dictionary<string, string> _activeProfileByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            StringBuilder ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        /// <summary>
        /// 使用 Windows 原生 DPAPI (CryptProtectData) 加密本机敏感凭据 (零外部 DLL 依赖)
        /// </summary>
        public static string ProtectSecretDpapi(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            if (plainText.StartsWith("DPAPI:", StringComparison.Ordinal)) return plainText;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            GCHandle handle = GCHandle.Alloc(plainBytes, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = plainBytes.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (CryptProtectData(ref inBlob, "XiaoLvExoskeletonVault", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    byte[] cipherBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
                    return "DPAPI:" + Convert.ToBase64String(cipherBytes);
                }
            }
            catch { }
            finally
            {
                if (handle.IsAllocated) handle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
            return "";
        }

        /// <summary>
        /// 使用 Windows 原生 DPAPI (CryptUnprotectData) 解密本机敏感凭据
        /// </summary>
        public static string UnprotectSecretDpapi(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";
            if (!cipherText.StartsWith("DPAPI:", StringComparison.Ordinal)) return cipherText;

            string b64 = cipherText.Substring("DPAPI:".Length);
            byte[] cipherBytes;
            try { cipherBytes = Convert.FromBase64String(b64); }
            catch { return ""; }

            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            GCHandle handle = GCHandle.Alloc(cipherBytes, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = cipherBytes.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (CryptUnprotectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    byte[] plainBytes = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
            catch { }
            finally
            {
                if (handle.IsAllocated) handle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
            return "";
        }

        /// <summary>
        /// 异步加载配置预设列表 (思源原生 SQLite + 本地脱机缓存双轨并集)
        /// </summary>
        public static async Task<List<SiYuanProfileEntry>> LoadProfilesAsync(string typeFilter = "")
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = LoadFromLocalCache();
                var remote = await QueryRemoteProfilesAsync(null, null, null).ConfigureAwait(false);
                if (!remote.Available) return FilterByType(cached, typeFilter);

                var list = new List<SiYuanProfileEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in remote.Entries)
                {
                    if (entry == null || _deletedProfileIds.Contains(entry.Id) || !seen.Add(entry.Id)) continue;
                    var local = cached.Find(x => x != null && string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                    if (local != null && local.IsLocalOnly)
                    {
                        // A failed update has a newer durable local copy. Keep it until explicitly saved again.
                        if (string.IsNullOrEmpty(local.BlockId)) local.BlockId = entry.BlockId;
                        list.Add(local);
                    }
                    else
                    {
                        if (local != null)
                        {
                            if (string.IsNullOrEmpty(entry.Description)) entry.Description = local.Description;
                            if (string.IsNullOrEmpty(entry.CreatedTime)) entry.CreatedTime = local.CreatedTime;
                        }
                        list.Add(entry);
                    }
                }
                // Keep durable local entries while the kernel index catches up, and keep offline presets.
                foreach (var entry in cached)
                {
                    if (entry != null && !_deletedProfileIds.Contains(entry.Id) && seen.Add(entry.Id)) list.Add(entry);
                }
                SaveToLocalCache(list);
                return FilterByType(list, typeFilter);
            }
            finally { _mutationGate.Release(); }
        }

        /// <summary>
        /// 保存或追加一条配置预设 (自动脱敏并沉淀至原生文档与本地缓存)
        /// </summary>
        public static async Task<bool> SaveProfileAsync(SiYuanProfileEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return false;
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                entry.Name = entry.Name.Trim();
                entry.Type = (entry.Type ?? "s3").Trim().ToLowerInvariant();
                entry.ConfigData = entry.ConfigData ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var cached = LoadFromLocalCache();
                var previous = cached.Find(x => x != null && string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ??
                    cached.Find(x => SameProfileNameAndType(x, entry.Name, entry.Type));
                if (previous != null)
                {
                    entry.Id = previous.Id;
                    entry.BlockId = previous.BlockId;
                    entry.CreatedTime = previous.CreatedTime;
                }
                if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                entry.UpdatedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

                var remote = await QueryRemoteProfilesAsync(entry.Id, entry.Name, entry.Type).ConfigureAwait(false);
                var remoteEntry = remote.Entries.Find(x => string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase)) ??
                    remote.Entries.Find(x => SameProfileNameAndType(x, entry.Name, entry.Type));
                if (remote.Available && remoteEntry == null && IsSiYuanBlockId(entry.BlockId) &&
                    await VerifyRemoteProfileBlockAsync(entry.BlockId, entry.Id).ConfigureAwait(false))
                {
                    remoteEntry = new SiYuanProfileEntry { Id = entry.Id, BlockId = entry.BlockId, Name = entry.Name, Type = entry.Type };
                }
                if (remoteEntry != null)
                {
                    entry.Id = remoteEntry.Id;
                    entry.BlockId = remoteEntry.BlockId;
                }
                _deletedProfileIds.Remove(entry.Id);

                int storedCredentials = SaveCredentialsToDpapiVault(entry.Id, entry.Name, entry.ConfigData);
                // Failure payloads are serialized by both host bridges, so redact even when persistence failed.
                SanitizeProfileConfigData(entry.ConfigData);
                if (storedCredentials < 0) return false;
                entry.HasVaultCredentials = HasDpapiVaultCredentials(entry.Id, entry.Name);
                entry.IsLocalOnly = true;
                if (!StoreCachedProfile(entry)) return false;

                // A query failure must not turn an uncertain update into a new append.
                if (!remote.Available) return true;
                string markdown = BuildProfileMarkdown(entry);
                SiYuanResponse response;
                if (remoteEntry != null && IsSiYuanBlockId(remoteEntry.BlockId))
                {
                    response = await SiYuanClient.PostAsync("/api/block/updateBlock", new
                    {
                        id = remoteEntry.BlockId,
                        dataType = "markdown",
                        data = markdown
                    }).ConfigureAwait(false);
                }
                else
                {
                    // A known block absent from the current index may be temporarily unindexed; do not duplicate it.
                    if (IsSiYuanBlockId(entry.BlockId)) return true;
                    string docId = await EnsureProfileDocExistsAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(docId)) return true;
                    response = await SiYuanClient.PostAsync("/api/block/appendBlock", new
                    {
                        parentID = docId,
                        dataType = "markdown",
                        data = markdown
                    }).ConfigureAwait(false);
                    if (response != null && response.Success)
                    {
                        entry.BlockId = GetInsertedBlockId(response);
                        // 立即提交内核事务：连续快速重复保存同名预设时，下一次按名检索必须能看到刚写入的块，
                        // 否则会误判不存在而重复建档（E2E 7.121.1 偶发根因，与离线补录 7.67 同类）
                        try { await SiYuanClient.PostAsync("/api/sqlite/flushTransaction", new { }).ConfigureAwait(false); } catch { }
                    }
                }
                if (response == null || !response.Success) return true;
                entry.IsLocalOnly = false;
                return StoreCachedProfile(entry);
            }
            catch (Exception ex)
            {
                SanitizeProfileConfigData(entry.ConfigData);
                Logger.Error("Saving profile failed", ex);
                return false;
            }
            finally { _mutationGate.Release(); }
        }

        private static bool StoreCachedProfile(SiYuanProfileEntry entry)
        {
            lock (_lock)
            {
                var cached = LoadFromLocalCache();
                cached.RemoveAll(x => x != null && (string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase) ||
                    SameProfileNameAndType(x, entry.Name, entry.Type)));
                cached.Insert(0, entry);
                return SaveToLocalCache(cached);
            }
        }

        private static string BuildProfileMarkdown(SiYuanProfileEntry entry)
        {
            return "```json\n" + JsonHelper.ToJson(entry.ConfigData) + "\n```\n" +
                "{: custom-type=\"siyuan-profile\" custom-profile-id=\"" + EscapeIal(entry.Id) +
                "\" custom-profile-type=\"" + EscapeIal(entry.Type) +
                "\" custom-profile-name=\"" + EscapeIal(entry.Name) +
                "\" custom-profile-description=\"" + EscapeIal(entry.Description) +
                "\" custom-profile-created=\"" + EscapeIal(entry.CreatedTime) +
                "\" custom-profile-updated=\"" + EscapeIal(entry.UpdatedTime) + "\" }\n";
        }

        private static bool SameProfileNameAndType(SiYuanProfileEntry entry, string name, string type)
        {
            return entry != null && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSiYuanBlockId(string id)
        {
            return !string.IsNullOrEmpty(id) && Regex.IsMatch(id, @"^\d{14}-[a-z0-9]{7}$");
        }

        private static string GetInsertedBlockId(SiYuanResponse response)
        {
            var operations = JsonHelper.GetList(new Dictionary<string, object> { { "operations", response.Data } }, "operations");
            if (operations == null) return "";
            foreach (var operation in operations)
            {
                var dict = operation as Dictionary<string, object>;
                var doOperations = JsonHelper.GetList(dict, "doOperations");
                if (doOperations == null) continue;
                foreach (var doOperation in doOperations)
                {
                    string blockId = JsonHelper.GetString(doOperation as Dictionary<string, object>, "id");
                    if (IsSiYuanBlockId(blockId)) return blockId;
                }
            }
            return "";
        }

        private class RemoteProfileSnapshot
        {
            public bool Available;
            public List<SiYuanProfileEntry> Entries = new List<SiYuanProfileEntry>();
        }

        private static async Task<RemoteProfileSnapshot> QueryRemoteProfilesAsync(string profileId, string name, string type)
        {
            var result = new RemoteProfileSnapshot();
            string filter = "";
            if (!string.IsNullOrEmpty(profileId))
            {
                filter = " AND (b.ial LIKE '%custom-profile-id=\"" + EscapeSqlLike(profileId) + "\"%'";
                if (!string.IsNullOrEmpty(name))
                {
                    filter += " OR (b.ial LIKE '%custom-profile-name=\"" + EscapeSqlLike(EscapeIal(name)) + "\"%'" +
                        " AND b.ial LIKE '%custom-profile-type=\"" + EscapeSqlLike(EscapeIal(type ?? "s3")) + "\"%')";
                }
                if (IsSiYuanBlockId(profileId)) filter += " OR b.id='" + profileId + "'";
                filter += ")";
            }
            string sql = "SELECT b.id, b.content, b.markdown, b.ial, b.updated FROM blocks b " +
                "WHERE b.type != 'd' AND b.ial LIKE '%custom-type=\"siyuan-profile\"%'" + filter +
                " ORDER BY b.updated DESC LIMIT 500";
            var response = await SiYuanClient.PostAsync("/api/query/sql", new { stmt = sql }).ConfigureAwait(false);
            if (response == null || !response.Success) return result;
            result.Available = true;
            var rows = JsonHelper.GetList(new Dictionary<string, object> { { "rows", response.Data } }, "rows");
            if (rows == null) return result;
            foreach (var row in rows)
            {
                var entry = ParseBlockToEntry(row as Dictionary<string, object>);
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(profileId) && !string.Equals(entry.Id, profileId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry.BlockId, profileId, StringComparison.OrdinalIgnoreCase) &&
                    !SameProfileNameAndType(entry, name, type)) continue;
                result.Entries.Add(entry);
            }
            return result;
        }

        private static string EscapeSqlLike(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        private static async Task<bool> VerifyRemoteProfileBlockAsync(string blockId, string logicalId)
        {
            if (!IsSiYuanBlockId(blockId)) return false;
            var response = await SiYuanClient.PostAsync("/api/attr/getBlockAttrs", new { id = blockId }).ConfigureAwait(false);
            if (response == null || !response.Success) return false;
            var attributes = response.Data as Dictionary<string, object>;
            if (attributes == null) return false;
            return JsonHelper.GetString(attributes, "custom-type") == "siyuan-profile" &&
                string.Equals(JsonHelper.GetString(attributes, "custom-profile-id", blockId), logicalId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 删除指定的配置预设
        /// </summary>
        public static async Task<bool> DeleteProfileAsync(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return false;
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = LoadFromLocalCache();
                var removedEntry = cached.Find(x => x != null &&
                    (string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.BlockId, profileId, StringComparison.OrdinalIgnoreCase)));
                string logicalId = removedEntry != null ? removedEntry.Id : profileId;
                var remote = await QueryRemoteProfilesAsync(logicalId, null, null).ConfigureAwait(false);
                if (!remote.Available && (removedEntry == null || !removedEntry.IsLocalOnly || IsSiYuanBlockId(removedEntry.BlockId))) return false;
                if (remote.Available && remote.Entries.Count == 0 && removedEntry != null && IsSiYuanBlockId(removedEntry.BlockId))
                {
                    // The block tree may have committed before SQLite indexing. Verify its own attributes before deleting it.
                    if (!await VerifyRemoteProfileBlockAsync(removedEntry.BlockId, logicalId).ConfigureAwait(false)) return false;
                    remote.Entries.Add(removedEntry);
                }

                var blockIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in remote.Entries)
                {
                    if (IsSiYuanBlockId(entry.BlockId) && blockIds.Add(entry.BlockId))
                    {
                        var response = await SiYuanClient.PostAsync("/api/block/deleteBlock", new { id = entry.BlockId }).ConfigureAwait(false);
                        if (response == null || !response.Success) return false;
                    }
                    if (removedEntry == null) removedEntry = entry;
                }
                if (removedEntry == null) return false;

                // Do not remove the local copy until remote deletion has succeeded, and do not restore stale whole-cache snapshots.
                lock (_lock)
                {
                    var current = LoadFromLocalCache();
                    current.RemoveAll(x => x != null && string.Equals(x.Id, logicalId, StringComparison.OrdinalIgnoreCase));
                    if (!SaveToLocalCache(current)) return false;
                }
                // The kernel's SQLite index may still return the just-deleted row on the next UI refresh.
                _deletedProfileIds.Add(logicalId);
                return RemoveProfileCredentialsFromDpapiVault(removedEntry.Id, removedEntry.Name, removedEntry.Type);
            }
            catch (Exception ex)
            {
                Logger.Error("Deleting profile failed; any unconfirmed local record was preserved", ex);
                return false;
            }
            finally { _mutationGate.Release(); }
        }

        private static bool RemoveProfileCredentialsFromDpapiVault(string profileId, string profileName, string profileType)
        {
            try
            {
                lock (_lock)
                {
                    var vault = LoadDpapiVaultDict();
                    bool changed = false;
                    if (!string.IsNullOrEmpty(profileId)) changed |= vault.Remove("id:" + profileId);
                    bool nameStillUsed = LoadFromLocalCache().Exists(x => x != null &&
                        string.Equals(x.Name, profileName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(profileName) && !nameStillUsed) changed |= vault.Remove("name:" + profileName);
                    string typeKey = profileType ?? "s3";
                    string activeKey = "active:" + typeKey.ToLowerInvariant();
                    bool clearActive = string.Equals(JsonHelper.GetString(vault, activeKey), profileId, StringComparison.OrdinalIgnoreCase);
                    if (clearActive)
                    {
                        changed |= vault.Remove(activeKey);
                    }
                    if (changed && !SaveDpapiVaultDict(vault)) return false;
                    if (clearActive) _activeProfileByType.Remove(typeKey);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Removing local profile credentials failed", ex);
                return false;
            }
        }

        /// <summary>
        /// 智能嗅探当前思源 live 配置并一键萃取脱敏预设 (s3 / ai / webdav)
        /// </summary>
        public static SiYuanProfileEntry CaptureCurrentLiveProfile(string type, string customName = "")
        {
            var entry = new SiYuanProfileEntry
            {
                Type = (type ?? "s3").Trim().ToLowerInvariant()
            };

            try
            {
                string ws = SiYuanClient.DiscoveredWorkspaceDir;
                if (!string.IsNullOrEmpty(ws))
                {
                    string confPath = Path.Combine(ws, "conf", "conf.json");
                    if (File.Exists(confPath))
                    {
                        string text = File.ReadAllText(confPath, Encoding.UTF8);
                        var conf = JsonHelper.ParseDict(text);

                        if (entry.Type == "s3")
                        {
                            var sync = JsonHelper.GetDict(conf, "sync");
                            var s3 = sync != null ? JsonHelper.GetDict(sync, "s3") : null;
                            if (s3 != null)
                            {
                                entry.ConfigData = new Dictionary<string, object>(s3, StringComparer.OrdinalIgnoreCase);
                                entry.Name = !string.IsNullOrEmpty(customName) ? customName : ("S3同步预设 · " + JsonHelper.GetString(s3, "bucket", "存储桶"));
                                entry.Description = string.Format("Endpoint: {0} · Region: {1}", JsonHelper.GetString(s3, "endpoint"), JsonHelper.GetString(s3, "region"));
                            }
                        }
                        else if (entry.Type == "webdav")
                        {
                            var sync = JsonHelper.GetDict(conf, "sync");
                            var dav = sync != null ? JsonHelper.GetDict(sync, "webdav") : null;
                            if (dav != null)
                            {
                                entry.ConfigData = new Dictionary<string, object>(dav, StringComparer.OrdinalIgnoreCase);
                                entry.Name = !string.IsNullOrEmpty(customName) ? customName : "WebDAV同步预设";
                                entry.Description = "Endpoint: " + JsonHelper.GetString(dav, "endpoint");
                            }
                        }
                        else if (entry.Type == "ai")
                        {
                            var ai = JsonHelper.GetDict(conf, "ai");
                            if (ai != null)
                            {
                                var providers = JsonHelper.GetList(ai, "providers");
                                if (providers != null && providers.Count > 0)
                                {
                                    var first = providers[0] as Dictionary<string, object>;
                                    if (first != null)
                                    {
                                        entry.ConfigData = new Dictionary<string, object>(first, StringComparer.OrdinalIgnoreCase);
                                        entry.Name = !string.IsNullOrEmpty(customName) ? customName : ("AI模型预设 · " + JsonHelper.GetString(first, "displayName", "Default"));
                                        entry.Description = "BaseURL: " + JsonHelper.GetString(first, "baseURL");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            int storedCredentials = SaveCredentialsToDpapiVault(entry.Id, entry.Name, entry.ConfigData);
            if (storedCredentials < 0)
            {
                SanitizeProfileConfigData(entry.ConfigData);
                entry.HasVaultCredentials = false;
                entry.CaptureWarning = "真实凭据未能保存到本机 DPAPI 保险箱；预览已脱敏，未创建预设。请检查磁盘权限后重试。";
                return entry;
            }
            SanitizeProfileConfigData(entry.ConfigData);
            entry.HasVaultCredentials = HasDpapiVaultCredentials(entry.Id, entry.Name);
            if (string.IsNullOrEmpty(entry.Name))
            {
                entry.Name = (type.ToUpperInvariant() + " 预设快照 " + DateTime.Now.ToString("MM-dd HH:mm"));
            }

            return entry;
        }

        private static readonly string[] _sensitiveKeys = new string[]
        {
            "accessKey", "secretKey", "apiKey", "password", "token", "authCode", "access_key", "secret_key", "api_key"
        };

        private static Dictionary<string, object> LoadDpapiVaultDict()
        {
            string path = DpapiVaultPath;
            if (!File.Exists(path)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            string json = File.ReadAllText(path, Encoding.UTF8);
            var dict = JsonHelper.FromJson<Dictionary<string, object>>(json);
            if (dict == null) throw new InvalidDataException("本机 DPAPI 凭据保险箱格式无效。");
            return new Dictionary<string, object>(dict, StringComparer.OrdinalIgnoreCase);
        }

        private static bool SaveDpapiVaultDict(Dictionary<string, object> dict)
        {
            string tempPath = null;
            try
            {
                string json = JsonHelper.ToJson(dict);
                string targetPath = DpapiVaultPath;
                string directory = Path.GetDirectoryName(targetPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(targetPath)) File.Replace(tempPath, targetPath, null);
                else File.Move(tempPath, targetPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Saving local DPAPI credential vault failed", ex);
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
        }

        /// <summary>
        /// 将配置字典中的真实敏感凭据使用 Windows DPAPI 加密存入本机保险箱
        /// </summary>
        public static int SaveCredentialsToDpapiVault(string profileId, string profileName, Dictionary<string, object> data)
        {
            if (data == null) return 0;

            int savedCount = 0;
            try
            {
                lock (_lock)
                {
                    var vault = LoadDpapiVaultDict();
                    string keyById = "id:" + (profileId ?? "");
                    string keyByName = "name:" + (profileName ?? "");

                    var secretMap = JsonHelper.GetDict(vault, keyById) ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    bool protectionFailed = false;

                    foreach (var k in _sensitiveKeys)
                    {
                        if (data.ContainsKey(k) && data[k] != null)
                        {
                            string rawVal = data[k].ToString().Trim();
                            if (!string.IsNullOrEmpty(rawVal) && rawVal != "[SECURED_CREDENTIAL]")
                            {
                                string encrypted = ProtectSecretDpapi(rawVal);
                                if (!string.IsNullOrEmpty(encrypted))
                                {
                                    secretMap[k] = encrypted;
                                    savedCount++;
                                }
                                else protectionFailed = true;
                            }
                        }
                    }

                    if (protectionFailed) return -1;
                    if (savedCount > 0)
                    {
                        if (!string.IsNullOrEmpty(profileId)) vault[keyById] = secretMap;
                        if (!string.IsNullOrEmpty(profileName)) vault[keyByName] = secretMap;
                        if (!SaveDpapiVaultDict(vault)) return -1;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Storing profile credentials failed", ex);
                return -1;
            }
            return savedCount;
        }

        /// <summary>
        /// 从本机 DPAPI 保险箱解密还原敏感凭据至目标字典
        /// </summary>
        public static int RestoreCredentialsFromDpapiVault(string profileId, string profileName, Dictionary<string, object> targetData)
        {
            if (targetData == null) return 0;

            int restored = 0;
            lock (_lock)
            {
                var vault = LoadDpapiVaultDict();
                Dictionary<string, object> secretMap = null;
                if (!string.IsNullOrEmpty(profileId)) secretMap = JsonHelper.GetDict(vault, "id:" + profileId);
                if (secretMap == null && !string.IsNullOrEmpty(profileName)) secretMap = JsonHelper.GetDict(vault, "name:" + profileName);
                if (secretMap == null) return 0;

                foreach (var kv in secretMap)
                {
                    string cipher = kv.Value != null ? kv.Value.ToString() : "";
                    if (!string.IsNullOrEmpty(cipher))
                    {
                        string plain = UnprotectSecretDpapi(cipher);
                        if (!string.IsNullOrEmpty(plain))
                        {
                            targetData[kv.Key] = plain;
                            restored++;
                        }
                    }
                }
            }
            return restored;
        }

        public static bool HasDpapiVaultCredentials(string profileId, string profileName)
        {
            try
            {
                lock (_lock)
                {
                    var vault = LoadDpapiVaultDict();
                    if (!string.IsNullOrEmpty(profileId) && JsonHelper.GetDict(vault, "id:" + profileId) != null) return true;
                    if (!string.IsNullOrEmpty(profileName) && JsonHelper.GetDict(vault, "name:" + profileName) != null) return true;
                    return false;
                }
            }
            catch (Exception ex) { Logger.Error("Reading profile credential state failed", ex); return false; }
        }

        /// <summary>
        /// 一键热切换激活指定配置预设：自动从本机 DPAPI 保险箱解密凭据并注入运行时生效
        /// </summary>
        public static async Task<ProfileApplyResult> ApplyProfileToLiveAsync(string profileId)
        {
            var res = new ProfileApplyResult { ProfileId = profileId ?? "" };
            var all = await LoadProfilesAsync("all");
            if (all == null || all.Count == 0) return res;

            SiYuanProfileEntry target = all.Find(x => x != null && (x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(profileId, StringComparison.OrdinalIgnoreCase)));
            if (target == null && string.IsNullOrEmpty(profileId))
            {
                target = all[0];
            }
            if (target == null) return res;

            res.ProfileId = target.Id;
            res.ProfileName = target.Name;
            res.ProfileType = target.Type;

            var effectiveData = new Dictionary<string, object>(target.ConfigData ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            int restoredCount = 0;
            string activeKey = "active:" + (target.Type ?? "s3").ToLowerInvariant();
            string previousActiveId = "";
            bool activeMarkerSaved = false;
            try
            {
                restoredCount = RestoreCredentialsFromDpapiVault(target.Id, target.Name, effectiveData);
                res.RestoredCredentialCount = restoredCount;
                res.HasRestoredCredentials = restoredCount > 0;

                lock (_lock)
                {
                    var vault = LoadDpapiVaultDict();
                    previousActiveId = JsonHelper.GetString(vault, activeKey);
                    vault[activeKey] = target.Id;
                    if (!SaveDpapiVaultDict(vault))
                    {
                        res.AppliedSummary = "激活失败：本机无法保存预设状态，当前配置保持不变。";
                        return res;
                    }
                    _activeProfileByType[target.Type ?? "s3"] = target.Id;
                    activeMarkerSaved = true;
                }

                if (target.Type.Equals("ai", StringComparison.OrdinalIgnoreCase))
                {
                    string baseUrl = JsonHelper.GetString(effectiveData, "baseURL");
                    if (string.IsNullOrEmpty(baseUrl)) baseUrl = JsonHelper.GetString(effectiveData, "baseUrl");
                    if (string.IsNullOrEmpty(baseUrl)) baseUrl = JsonHelper.GetString(effectiveData, "endpoint");

                    string model = JsonHelper.GetString(effectiveData, "modelName");
                    if (string.IsNullOrEmpty(model)) model = JsonHelper.GetString(effectiveData, "model");

                    string apiKey = JsonHelper.GetString(effectiveData, "apiKey");
                    if (string.IsNullOrEmpty(apiKey)) apiKey = JsonHelper.GetString(effectiveData, "api_key");

                    var settings = AppSettings.Instance;
                    string oldBaseUrl = settings.AiBaseUrl;
                    string oldModel = settings.AiModel;
                    string oldApiKey = settings.AiApiKey;
                    bool oldEnabled = settings.AiEnabled;
                    if (!string.IsNullOrEmpty(baseUrl)) settings.AiBaseUrl = baseUrl;
                    if (!string.IsNullOrEmpty(model)) settings.AiModel = model;
                    if (!string.IsNullOrEmpty(apiKey) && apiKey != "[SECURED_CREDENTIAL]")
                    {
                        settings.AiApiKey = apiKey;
                    }
                    settings.AiEnabled = true;
                    if (!settings.Save())
                    {
                        settings.AiBaseUrl = oldBaseUrl;
                        settings.AiModel = oldModel;
                        settings.AiApiKey = oldApiKey;
                        settings.AiEnabled = oldEnabled;
                        RestoreActiveProfileMarker(activeKey, previousActiveId, target.Type);
                        activeMarkerSaved = false;
                        res.AppliedSummary = "激活失败：AI 设置未能保存，原配置保持不变。";
                        return res;
                    }

                    res.AppliedSummary = string.Format("AI 驱动源已热切换为【{0}】({1}){2}",
                        target.Name,
                        !string.IsNullOrEmpty(model) ? model : "默认模型",
                        restoredCount > 0 ? " · 已从本机 DPAPI 保险箱自动注入加密密钥" : "");
                }
                else if (target.Type.Equals("s3", StringComparison.OrdinalIgnoreCase))
                {
                    string bucket = JsonHelper.GetString(effectiveData, "bucket", "默认桶");
                    string endpoint = JsonHelper.GetString(effectiveData, "endpoint", "");
                    res.AppliedSummary = string.Format("S3 存储桶预设已切换为【{0}】(Bucket: {1}){2}",
                        target.Name,
                        bucket,
                        restoredCount > 0 ? string.Format(" · 已从 DPAPI 保险箱还原 {0} 项密钥", restoredCount) : "");
                }
                else
                {
                    res.AppliedSummary = string.Format("配置预设【{0}】({1}) 已激活生效", target.Name, target.Type.ToUpperInvariant());
                }

                res.Success = true;
                OmniNotificationHub.Push("配置预设一键热切换", res.AppliedSummary, "success", "siyuan", target.Id);
            }
            catch (Exception ex)
            {
                if (activeMarkerSaved) RestoreActiveProfileMarker(activeKey, previousActiveId, target.Type);
                res.AppliedSummary = "激活失败: " + ex.Message;
            }

            return res;
        }

        private static bool RestoreActiveProfileMarker(string key, string previousId, string profileType)
        {
            try
            {
                lock (_lock)
                {
                    var vault = LoadDpapiVaultDict();
                    if (string.IsNullOrEmpty(previousId)) vault.Remove(key);
                    else vault[key] = previousId;
                    if (!SaveDpapiVaultDict(vault)) return false;
                    string typeKey = profileType ?? "s3";
                    if (string.IsNullOrEmpty(previousId)) _activeProfileByType.Remove(typeKey);
                    else _activeProfileByType[typeKey] = previousId;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Restoring active profile marker failed", ex);
                return false;
            }
        }

        /// <summary>
        /// 严格凭据脱敏过滤（移除或屏蔽真实密码与私有密钥）
        /// </summary>
        public static void SanitizeProfileConfigData(Dictionary<string, object> data)
        {
            if (data == null) return;

            var sensitiveKeys = new string[]
            {
                "accessKey", "secretKey", "apiKey", "password", "token", "authCode", "access_key", "secret_key", "api_key"
            };

            foreach (var k in sensitiveKeys)
            {
                if (data.ContainsKey(k))
                {
                    data[k] = "[SECURED_CREDENTIAL]";
                }
            }
        }

        /// <summary>
        /// 智能关键词检索配置预设
        /// </summary>
        public static List<SiYuanProfileEntry> Search(IEnumerable<SiYuanProfileEntry> pool, string keyword, string typeFilter = "")
        {
            var results = new List<SiYuanProfileEntry>();
            if (pool == null) return results;

            string cleanKw = (keyword ?? "").Trim().ToLowerInvariant();

            foreach (var it in pool)
            {
                if (it == null) continue;
                if (!string.IsNullOrEmpty(typeFilter) && !typeFilter.Equals("all", StringComparison.OrdinalIgnoreCase) && !it.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

                if (string.IsNullOrEmpty(cleanKw))
                {
                    results.Add(it);
                    continue;
                }

                bool match = it.Name.ToLowerInvariant().Contains(cleanKw) ||
                             it.Type.ToLowerInvariant().Contains(cleanKw) ||
                             it.Description.ToLowerInvariant().Contains(cleanKw) ||
                             JsonHelper.ToJson(it.ConfigData).ToLowerInvariant().Contains(cleanKw);

                if (match)
                {
                    results.Add(it);
                }
            }

            return results;
        }

        private static List<SiYuanProfileEntry> FilterByType(List<SiYuanProfileEntry> list, string typeFilter)
        {
            if (list != null)
            {
                Dictionary<string, object> vault = null;
                var activeProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                lock (_lock)
                {
                    try { vault = LoadDpapiVaultDict(); }
                    catch (Exception ex) { Logger.Error("Reading preset credential badges failed; preserving the vault file", ex); }
                    foreach (var kv in _activeProfileByType) activeProfiles[kv.Key] = kv.Value;
                }

                foreach (var item in list)
                {
                    if (item == null) continue;
                    string tKey = "active:" + (item.Type ?? "s3").ToLowerInvariant();
                    string activeId = "";
                    if (activeProfiles.ContainsKey(item.Type ?? "s3"))
                    {
                        activeId = activeProfiles[item.Type ?? "s3"];
                    }
                    else if (vault != null)
                    {
                        activeId = JsonHelper.GetString(vault, tKey);
                    }
                    item.IsActive = !string.IsNullOrEmpty(activeId) && activeId.Equals(item.Id, StringComparison.OrdinalIgnoreCase);
                    item.HasVaultCredentials = vault != null &&
                        (JsonHelper.GetDict(vault, "id:" + item.Id) != null || JsonHelper.GetDict(vault, "name:" + item.Name) != null);
                }
            }
            if (string.IsNullOrEmpty(typeFilter) || typeFilter == "all") return list;
            return list.FindAll(x => x != null && x.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        private static SiYuanProfileEntry ParseBlockToEntry(Dictionary<string, object> row)
        {
            if (row == null) return null;
            string id = JsonHelper.GetString(row, "id");
            if (!IsSiYuanBlockId(id)) return null;
            string content = JsonHelper.GetString(row, "content");
            string markdown = JsonHelper.GetString(row, "markdown");
            string ial = JsonHelper.GetString(row, "ial");

            string text = !string.IsNullOrEmpty(markdown) ? markdown : content;
            string fullMeta = ial + " " + text;
            if (ExtractAttr(ial, "custom-type", "") != "siyuan-profile") return null;
            var entry = new SiYuanProfileEntry { Id = id, BlockId = id, IsLocalOnly = false };
            entry.Name = ExtractAttr(fullMeta, "custom-profile-name", "");
            if (string.IsNullOrEmpty(entry.Name))
            {
                var nameMatch = Regex.Match(text, @"\*\*([^*]+)\*\*");
                if (nameMatch.Success) entry.Name = nameMatch.Groups[1].Value.Trim();
            }
            entry.Type = ExtractAttr(fullMeta, "custom-profile-type", "s3");
            entry.Id = ExtractAttr(fullMeta, "custom-profile-id", id);
            entry.Description = ExtractAttr(fullMeta, "custom-profile-description", "");
            entry.CreatedTime = ExtractAttr(fullMeta, "custom-profile-created", "");
            entry.UpdatedTime = ExtractAttr(fullMeta, "custom-profile-updated", JsonHelper.GetString(row, "updated"));

            // 提取 JSON 代码块中的配置参数
            var jsonMatch = Regex.Match(text, @"```json\s*([\s\S]*?)\s*```");
            if (jsonMatch.Success)
            {
                entry.ConfigData = JsonHelper.ParseDict(jsonMatch.Groups[1].Value);
            }
            else if (!string.IsNullOrWhiteSpace(content) && content.TrimStart().StartsWith("{"))
            {
                entry.ConfigData = JsonHelper.ParseDict(content);
            }
            SanitizeProfileConfigData(entry.ConfigData);

            if (string.IsNullOrEmpty(entry.Name)) return null;
            return entry;
        }

        private static string ExtractAttr(string src, string attrName, string defVal)
        {
            if (string.IsNullOrEmpty(src)) return defVal;
            var m = Regex.Match(src, @"(?:^|\s)" + Regex.Escape(attrName) + @"=""((?:\\.|[^""\\])*)""");
            if (m.Success) return WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            return defVal;
        }

        private static string EscapeIal(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return WebUtility.HtmlEncode(s.Replace("\r", " ").Replace("\n", " "));
        }

        private static async Task<string> EnsureProfileDocExistsAsync()
        {
            try
            {
                var rows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='d' AND hpath LIKE '%/配置预设%' LIMIT 1");
                if (rows != null && rows.Count > 0)
                {
                    return JsonHelper.GetString(rows[0], "id");
                }

                string nbId = "";
                var boxRows = await SiYuanClient.SqlAsync("SELECT box FROM blocks WHERE box != '' LIMIT 1");
                if (boxRows != null && boxRows.Count > 0)
                {
                    nbId = JsonHelper.GetString(boxRows[0], "box");
                }

                if (string.IsNullOrEmpty(nbId))
                {
                    var notebooks = await SiYuanClient.LsNotebooksAsync();
                    if (notebooks != null && notebooks.Count > 0)
                    {
                        var openNb = notebooks.Find(x => !x.Closed);
                        nbId = openNb != null ? openNb.Id : notebooks[0].Id;
                    }
                }

                if (!string.IsNullOrEmpty(nbId))
                {
                    var res = await SiYuanClient.PostAsync("/api/filetree/createDocWithMd", new
                    {
                        notebook = nbId,
                        path = TARGET_DOC_PATH,
                        markdown = "# ⚙️ 思源常用配置与模型预设中枢\n\n> 💡 由思源外骨骼统一调度，严格脱敏保存 S3、WebDAV、大模型 API 等常用配置预设，支持一键切换。\n\n"
                    });

                    if (res != null && res.Success && res.Data != null)
                    {
                        return res.Data.ToString();
                    }

                    var queryRows = await SiYuanClient.SqlAsync("SELECT id FROM blocks WHERE type='d' AND hpath LIKE '%/配置预设%' LIMIT 1");
                    if (queryRows != null && queryRows.Count > 0)
                    {
                        return JsonHelper.GetString(queryRows[0], "id");
                    }
                }
            }
            catch { }

            return "";
        }

        public static List<SiYuanProfileEntry> LoadFromLocalCache()
        {
            lock (_lock)
            {
                string p = LocalCachePath;
                if (File.Exists(p))
                {
                    try
                    {
                        string json = File.ReadAllText(p, Encoding.UTF8);
                        var list = JsonHelper.FromJson<List<SiYuanProfileEntry>>(json);
                        if (list == null) throw new InvalidDataException("本地配置预设缓存格式无效。");
                        return list;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Reading local profile cache failed", ex);
                        return new List<SiYuanProfileEntry>();
                    }
                }

                // 默认内置主流云厂商与大模型开箱即用模板种子
                var seeds = new List<SiYuanProfileEntry>
                {
                    new SiYuanProfileEntry
                    {
                        Name = "Cloudflare R2 生产库 (0 出网流费)",
                        Type = "s3",
                        Description = "Endpoint: https://<account_id>.r2.cloudflarestorage.com · Region: auto",
                        ConfigData = new Dictionary<string, object>
                        {
                            { "endpoint", "https://<account_id>.r2.cloudflarestorage.com" },
                            { "bucket", "siyuan-vault-prod" },
                            { "region", "auto" },
                            { "pathStyle", true },
                            { "skipTlsVerify", false },
                            { "timeout", 60 },
                            { "concurrentReqs", 8 }
                        }
                    },
                    new SiYuanProfileEntry
                    {
                        Name = "MinIO 私有化 NAS 存储 (局域网极速)",
                        Type = "s3",
                        Description = "Endpoint: http://192.168.1.100:9000 · Region: us-east-1",
                        ConfigData = new Dictionary<string, object>
                        {
                            { "endpoint", "http://192.168.1.100:9000" },
                            { "bucket", "siyuan-backup" },
                            { "region", "us-east-1" },
                            { "pathStyle", true },
                            { "skipTlsVerify", true },
                            { "timeout", 30 },
                            { "concurrentReqs", 16 }
                        }
                    },
                    new SiYuanProfileEntry
                    {
                        Name = "DeepSeek 官方大模型 (V3/R1 极速推理)",
                        Type = "ai",
                        Description = "BaseURL: https://api.deepseek.com · Model: deepseek-chat",
                        ConfigData = new Dictionary<string, object>
                        {
                            { "displayName", "DeepSeek官方" },
                            { "baseURL", "https://api.deepseek.com" },
                            { "protocol", "openai" },
                            { "modelName", "deepseek-chat" },
                            { "requestTimeout", 60 }
                        }
                    },
                    new SiYuanProfileEntry
                    {
                        Name = "本地 Ollama 离线开源大模型 (零外网泄露)",
                        Type = "ai",
                        Description = "BaseURL: http://127.0.0.1:11434/v1 · Model: qwen2.5:32b",
                        ConfigData = new Dictionary<string, object>
                        {
                            { "displayName", "本地Ollama" },
                            { "baseURL", "http://127.0.0.1:11434/v1" },
                            { "protocol", "openai" },
                            { "modelName", "qwen2.5:32b" },
                            { "requestTimeout", 120 }
                        }
                    }
                };

                if (!SaveToLocalCache(seeds)) Logger.Error("Built-in profile seeds could not be saved locally.");
                return seeds;
            }
        }

        private static bool SaveToLocalCache(List<SiYuanProfileEntry> list)
        {
            lock (_lock)
            {
                string tempPath = null;
                try
                {
                    string json = JsonHelper.ToJson(list);
                    string targetPath = LocalCachePath;
                    if (File.Exists(targetPath))
                    {
                        var current = JsonHelper.FromJson<List<SiYuanProfileEntry>>(File.ReadAllText(targetPath, Encoding.UTF8));
                        if (current == null) throw new InvalidDataException("本地预设缓存无法解析；原文件已保留，停止覆盖。");
                    }
                    tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
                    File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                    if (File.Exists(targetPath)) File.Replace(tempPath, targetPath, null);
                    else File.Move(tempPath, targetPath);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error("Saving local profile cache failed", ex);
                    return false;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(tempPath))
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    }
                }
            }
        }
    }
}
