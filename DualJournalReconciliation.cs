using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FastLinkDirectTool.Modes
{
    public sealed class DualReconciliationEntry
    {
        public DualResource Resource { get; set; }
        public string ObservedValue { get; set; }
        public string Disposition { get; set; }
    }

    public sealed class DualReconciliationReport
    {
        public bool CanArchive { get; set; }
        public bool HasJournal { get; set; }
        public string Message { get; set; }
        public string Owner { get; set; }
        public string Scope { get; set; }
        public long Generation { get; set; }
        public DateTime ReviewedUtc { get; set; }
        public string JournalFingerprint { get; set; }
        public List<DualReconciliationEntry> Entries { get; set; }
        public DualReconciliationReport() { Entries = new List<DualReconciliationEntry>(); }
    }

    // Optional storage capability. It must compare the WHOLE current journal,
    // persist the evidence before the commit, and retain the original on failure.
    // It performs no network work and never restores old interface settings.
    public interface IDualJournalArchive
    {
        string ArchiveVerified(DualJournalDocument expected, DualReconciliationReport evidence);
    }

    public static class DualJournalFingerprint
    {
        public static string Compute(DualJournalDocument document)
        {
            if (document == null) throw new ArgumentNullException("document");
            byte[] bytes = Encoding.UTF8.GetBytes(Serializer().Serialize(document));
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        internal static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 32 }; }
    }

    public sealed partial class DualFileJournal : IDualJournalArchive
    {
        // Open with DELETE access but without FILE_SHARE_WRITE/DELETE. Renaming
        // through this SAME handle closes the check-to-rename race; File.Move
        // after closing an ordinary stream would leave a concurrent-write gap.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
            IntPtr information, uint size);

        public string ArchiveVerified(DualJournalDocument expected, DualReconciliationReport evidence)
        {
            lock (sync)
            {
                Validate(expected); CheckPath();
                string fingerprint = DualJournalFingerprint.Compute(expected);
                if (evidence == null || !evidence.CanArchive || !evidence.HasJournal ||
                    evidence.JournalFingerprint != fingerprint || evidence.Owner != expected.Owner ||
                    evidence.Scope != expected.Scope || evidence.Generation != expected.Generation ||
                    evidence.Entries == null || evidence.Entries.Count != expected.Entries.Count)
                    throw new InvalidDataException("核对证据与待归档事务不一致，原记录保留。");
                using (SafeFileHandle handle = CreateFile(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定原恢复记录，未归档。");
                    using (FileStream stream = new FileStream(handle, FileAccess.Read))
                    {
                        if (stream.Length < 2 || stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复记录大小异常。");
                        byte[] original = new byte[(int)stream.Length];
                        int count = 0, read;
                        while (count < original.Length && (read = stream.Read(original, count, original.Length - count)) > 0) count += read;
                        if (count != original.Length) throw new EndOfStreamException("恢复记录读取不完整。");
                        string json = Encoding.UTF8.GetString(original).TrimStart('\ufeff');
                        DualJournalDocument current = DualJournalFingerprint.Serializer().Deserialize<DualJournalDocument>(json);
                        Validate(current);
                        if (DualJournalFingerprint.Compute(current) != fingerprint)
                            throw new InvalidDataException("恢复记录已被其他操作改变，未归档。");
                        CheckPath();
                        string archiveRoot = Path.Combine(Path.GetDirectoryName(path), "journal-archive");
                        CheckArchiveDirectory(archiveRoot);
                        Directory.CreateDirectory(archiveRoot); CheckArchiveDirectory(archiveRoot);
                        string directory = Path.Combine(archiveRoot, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(directory); CheckArchiveDirectory(directory);
                        byte[] evidenceBytes = new UTF8Encoding(false).GetBytes(DualJournalFingerprint.Serializer().Serialize(evidence));
                        string evidencePath = Path.Combine(directory, "reconciliation.json");
                        using (FileStream output = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                        { output.Write(evidenceBytes, 0, evidenceBytes.Length); output.Flush(true); }
                        byte[] saved = File.ReadAllBytes(evidencePath);
                        if (saved.Length != evidenceBytes.Length) throw new IOException("归档证据未完整写入，原事务保留。");
                        for (int i = 0; i < saved.Length; i++)
                            if (saved[i] != evidenceBytes[i]) throw new IOException("归档证据读回不一致，原事务保留。");
                        CheckPath(); CheckArchiveDirectory(archiveRoot); CheckArchiveDirectory(directory);
                        string destination = Path.Combine(directory, "dual-session.json");
                        RenameLockedJournal(handle, destination);
                        // The rename is the commit. Do not perform fallible writes
                        // afterwards: original bytes and verified evidence persist.
                        return directory;
                    }
                }
            }
        }

        private static void CheckArchiveDirectory(string directory)
        {
            if (File.Exists(directory)) throw new IOException("归档目录位置被文件占用。");
            DirectoryInfo info = new DirectoryInfo(directory);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("归档目录被重定向，拒绝归档。");
        }

        private static void RenameLockedJournal(SafeFileHandle handle, string destination)
        {
            byte[] name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
            int rootOffset = IntPtr.Size == 8 ? 8 : 4;
            int lengthOffset = rootOffset + IntPtr.Size;
            int nameOffset = lengthOffset + 4;
            int size = checked(nameOffset + name.Length + 2);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
                Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                // FILE_RENAME_INFO, ReplaceIfExists=false. No destination is overwritten.
                if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new Win32Exception(error, "原恢复记录未能原子归档；未删除原文件。WindowsError=" + error);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
