using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace BelegpostMelder;

/// <summary>
/// „Standard-Mailprogramm“: öffnet die Mail per Simple MAPI (MAPISendMailW) im eingestellten Mailprogramm des PCs
/// (z. B. Thunderbird, eM Client, Outlook) – mit Empfängern, Betreff, Text und allen Anlagen. Der Benutzer sendet selbst.
/// Simple MAPI kennt nur Text: die CI-Gestaltung und das eingebettete Logo entfallen.
/// </summary>
internal static class MapiMail
{
    public sealed record Result(bool Ok, string Message, bool Fallback);

    /// <summary>Ist Belegpost selbst als MAPI-Mailprogramm eingetragen? Dann würde die Mail wieder bei Belegpost landen.</summary>
    public static bool DefaultIsBelegpost()
    {
        static string? Read(RegistryKey root)
        {
            try { using var k = root.OpenSubKey(@"Software\Clients\Mail"); return k?.GetValue(null) as string; } catch { return null; }
        }
        var name = Read(Registry.CurrentUser) ?? Read(Registry.LocalMachine);
        return name is not null && name.Contains("Belegpost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Mail-Daten wie /melder/api/vorgang/{id}/mail. Wartet höchstens ein paar Sekunden (manche Programme halten den Aufruf bis zum Senden offen).</summary>
    public static Result Open(JsonElement m, string folder)
    {
        if (DefaultIsBelegpost())
            return new Result(false, "Als Standard-Mailprogramm ist Belegpost selbst eingetragen (MAPI-Umleitung). Bitte im Portal beim Benutzer „Outlook“ oder „Outlook im Web“ wählen.", false);

        var files = new List<(string Path, string Name)>();
        foreach (var a in m.GetProperty("attachments").EnumerateArray())
        {
            if (!string.IsNullOrEmpty(Str(a, "contentId"))) continue; // eingebettetes Logo: ohne HTML sinnlos
            var name = Safe(Str(a, "name") ?? "Anhang");
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, Convert.FromBase64String(Str(a, "data") ?? ""));
            files.Add((path, name));
        }
        var recips = new List<(int Cls, string Addr)>();
        foreach (var (key, cls) in new[] { ("to", 1), ("cc", 2), ("bcc", 3) })
            if (m.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var x in list.EnumerateArray())
                    if (x.GetString() is { Length: > 0 } addr) recips.Add((cls, addr));
        var subject = Str(m, "subject") ?? "";
        var text = Str(m, "text") ?? "";

        int code = -1;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { code = Send(subject, text, recips, files); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Outlook hält den Aufruf bis zum Schließen des Fensters offen – läuft er nach 8 s noch, ist der Entwurf offen
        if (!thread.Join(TimeSpan.FromSeconds(8))) return new Result(true, "Im Mailprogramm geöffnet.", false);
        if (error is not null) return new Result(false, "MAPI nicht verfügbar: " + error.Message, true);
        return code switch
        {
            0 => new Result(true, "Im Mailprogramm geöffnet.", false),
            1 => new Result(true, "Im Mailprogramm geöffnet und wieder geschlossen.", false),
            2 or 3 or 26 => new Result(false, $"Kein Standard-Mailprogramm mit MAPI gefunden (MAPI-Fehler {code}).", true),
            _ => new Result(false, $"Das Mailprogramm meldet MAPI-Fehler {code}.", true)
        };
    }

    private static int Send(string subject, string text, List<(int Cls, string Addr)> recips, List<(string Path, string Name)> files)
    {
        var recipSize = Marshal.SizeOf<MapiRecipDescW>();
        var fileSize = Marshal.SizeOf<MapiFileDescW>();
        var pRecips = recips.Count == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(recipSize * recips.Count);
        var pFiles = files.Count == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(fileSize * files.Count);
        try
        {
            for (var i = 0; i < recips.Count; i++)
                Marshal.StructureToPtr(new MapiRecipDescW { RecipClass = recips[i].Cls, Name = recips[i].Addr, Address = "SMTP:" + recips[i].Addr }, pRecips + i * recipSize, false);
            for (var i = 0; i < files.Count; i++)
                Marshal.StructureToPtr(new MapiFileDescW { Position = -1, PathName = files[i].Path, FileName = files[i].Name }, pFiles + i * fileSize, false);
            var msg = new MapiMessageW
            {
                Subject = subject, NoteText = text,
                RecipCount = recips.Count, Recips = pRecips,
                FileCount = files.Count, Files = pFiles
            };
            return MAPISendMailW(IntPtr.Zero, IntPtr.Zero, msg, MAPI_LOGON_UI | MAPI_DIALOG, 0);
        }
        finally
        {
            for (var i = 0; i < recips.Count; i++) Marshal.DestroyStructure<MapiRecipDescW>(pRecips + i * recipSize);
            for (var i = 0; i < files.Count; i++) Marshal.DestroyStructure<MapiFileDescW>(pFiles + i * fileSize);
            if (pRecips != IntPtr.Zero) Marshal.FreeHGlobal(pRecips);
            if (pFiles != IntPtr.Zero) Marshal.FreeHGlobal(pFiles);
        }
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "Anhang" : name;
    }

    private const uint MAPI_LOGON_UI = 0x1, MAPI_DIALOG = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class MapiMessageW
    {
        public uint Reserved;
        public string? Subject;
        public string? NoteText;
        public string? MessageType;
        public string? DateReceived;
        public string? ConversationID;
        public uint Flags;
        public IntPtr Originator;
        public int RecipCount;
        public IntPtr Recips;
        public int FileCount;
        public IntPtr Files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiRecipDescW
    {
        public uint Reserved;
        public int RecipClass;
        public string? Name;
        public string? Address;
        public uint EIDSize;
        public IntPtr EntryID;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFileDescW
    {
        public uint Reserved;
        public uint Flags;
        public int Position;
        public string? PathName;
        public string? FileName;
        public IntPtr FileType;
    }

    [DllImport("mapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MAPISendMailW(IntPtr session, IntPtr uiParam, MapiMessageW message, uint flags, uint reserved);
}
