/*
 * BelegpostMapi.dll – Simple-MAPI-Mailclient „Belegpost“ für den ERP-/RDP-Server.
 *
 * Das ERP ruft MAPISendMail auf (über MAPI32.DLL → Standard-Mailclient). Diese DLL nimmt Empfänger, Betreff,
 * Text und Anhänge entgegen, liest die Anhänge sofort ein und übergibt alles über die lokale Named Pipe
 * \\.\pipe\belegpost-mapi an den Dienst „Belegpost Portal“. Der Dienst erkennt den Windows-Benutzer an der Pipe
 * und öffnet die Mail je nach Einstellung über den Belegpost Melder als Outlook-Entwurf auf dem PC des Benutzers.
 *
 * Ist der Dienst nicht erreichbar, wird die Nachricht in %LOCALAPPDATA%\Belegpost\mapi-ausgang abgelegt und beim
 * nächsten Aufruf nachgeliefert – es geht keine Mail verloren.
 *
 * Gebaut als 32- und 64-Bit-DLL (mingw-w64): x64 nach System32, x86 nach SysWOW64, registriert unter einem
 * gemeinsamen Pfad (Dateisystem-Umleitung wählt die passende Variante).
 */
#define WIN32_LEAN_AND_MEAN
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <wtsapi32.h>
#include <shlobj.h>
#include <stdio.h>
#include <ctype.h>
#include <stdlib.h>
#include <string.h>

#define EXPORT __declspec(dllexport)

typedef ULONG_PTR LHANDLE;
typedef ULONG FLAGS;

#define SUCCESS_SUCCESS 0
#define MAPI_USER_ABORT 1
#define MAPI_E_FAILURE 2
#define MAPI_E_INSUFFICIENT_MEMORY 5
#define MAPI_E_ATTACHMENT_NOT_FOUND 11
#define MAPI_E_NOT_SUPPORTED 26
#define MAPI_DIALOG 0x00000008
#define MAPI_ORIG 0
#define MAPI_TO 1
#define MAPI_CC 2
#define MAPI_BCC 3

typedef struct { ULONG ulReserved; ULONG ulRecipClass; LPSTR lpszName; LPSTR lpszAddress; ULONG ulEIDSize; LPVOID lpEntryID; } MapiRecipDescA;
typedef struct { ULONG ulReserved; ULONG flFlags; ULONG nPosition; LPSTR lpszPathName; LPSTR lpszFileName; LPVOID lpFileType; } MapiFileDescA;
typedef struct { ULONG ulReserved; LPSTR lpszSubject; LPSTR lpszNoteText; LPSTR lpszMessageType; LPSTR lpszDateReceived; LPSTR lpszConversationID;
    FLAGS flFlags; MapiRecipDescA *lpOriginator; ULONG nRecipCount; MapiRecipDescA *lpRecips; ULONG nFileCount; MapiFileDescA *lpFiles; } MapiMessageA;

typedef struct { ULONG ulReserved; ULONG ulRecipClass; PWSTR lpszName; PWSTR lpszAddress; ULONG ulEIDSize; PVOID lpEntryID; } MapiRecipDescW;
typedef struct { ULONG ulReserved; ULONG flFlags; ULONG nPosition; PWSTR lpszPathName; PWSTR lpszFileName; PVOID lpFileType; } MapiFileDescW;
typedef struct { ULONG ulReserved; PWSTR lpszSubject; PWSTR lpszNoteText; PWSTR lpszMessageType; PWSTR lpszDateReceived; PWSTR lpszConversationID;
    FLAGS flFlags; MapiRecipDescW *lpOriginator; ULONG nRecipCount; MapiRecipDescW *lpRecips; ULONG nFileCount; MapiFileDescW *lpFiles; } MapiMessageW;

static const wchar_t *PIPE_NAME = L"\\\\.\\pipe\\belegpost-mapi";
#define MAX_MESSAGE (60u * 1024u * 1024u)

/* ---------------- dynamischer Puffer ---------------- */

typedef struct { char *p; size_t len, cap; int failed; } Buf;

static void buf_grow(Buf *b, size_t need)
{
    if (b->failed) return;
    if (b->len + need + 1 <= b->cap) return;
    size_t cap = b->cap ? b->cap : 4096;
    while (cap < b->len + need + 1) cap *= 2;
    char *n = (char *)realloc(b->p, cap);
    if (!n) { b->failed = 1; return; }
    b->p = n; b->cap = cap;
}
static void buf_add(Buf *b, const char *s, size_t n) { buf_grow(b, n); if (b->failed) return; memcpy(b->p + b->len, s, n); b->len += n; b->p[b->len] = 0; }
static void buf_str(Buf *b, const char *s) { buf_add(b, s, strlen(s)); }

/* UTF-16 → UTF-8 (neu allokiert) */
static char *to_utf8(const wchar_t *w)
{
    if (!w) return NULL;
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    char *s = (char *)malloc(n > 0 ? n : 1);
    if (!s) return NULL;
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL); else s[0] = 0;
    return s;
}
/* ANSI (Systemcodepage) → UTF-16 (neu allokiert) */
static wchar_t *from_ansi(const char *a)
{
    if (!a) return NULL;
    int n = MultiByteToWideChar(CP_ACP, 0, a, -1, NULL, 0);
    wchar_t *w = (wchar_t *)malloc(sizeof(wchar_t) * (n > 0 ? n : 1));
    if (!w) return NULL;
    if (n > 0) MultiByteToWideChar(CP_ACP, 0, a, -1, w, n); else w[0] = 0;
    return w;
}

static void json_string_w(Buf *b, const wchar_t *w)
{
    if (!w) { buf_str(b, "null"); return; }
    char *s = to_utf8(w);
    if (!s) { b->failed = 1; return; }
    buf_str(b, "\"");
    for (const unsigned char *c = (const unsigned char *)s; *c; c++)
    {
        char tmp[8];
        switch (*c)
        {
            case '"': buf_str(b, "\\\""); break;
            case '\\': buf_str(b, "\\\\"); break;
            case '\n': buf_str(b, "\\n"); break;
            case '\r': buf_str(b, "\\r"); break;
            case '\t': buf_str(b, "\\t"); break;
            default:
                if (*c < 0x20) { sprintf(tmp, "\\u%04x", *c); buf_str(b, tmp); }
                else buf_add(b, (const char *)c, 1);
        }
    }
    buf_str(b, "\"");
    free(s);
}

static void json_base64(Buf *b, const unsigned char *d, size_t n)
{
    static const char t[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    buf_grow(b, ((n + 2) / 3) * 4 + 2);
    if (b->failed) return;
    buf_str(b, "\"");
    size_t i = 0;
    char q[4];
    for (; i + 2 < n; i += 3)
    {
        q[0] = t[d[i] >> 2]; q[1] = t[((d[i] & 3) << 4) | (d[i + 1] >> 4)]; q[2] = t[((d[i + 1] & 15) << 2) | (d[i + 2] >> 6)]; q[3] = t[d[i + 2] & 63];
        buf_add(b, q, 4);
    }
    if (i < n)
    {
        q[0] = t[d[i] >> 2];
        if (i + 1 < n) { q[1] = t[((d[i] & 3) << 4) | (d[i + 1] >> 4)]; q[2] = t[(d[i + 1] & 15) << 2]; }
        else { q[1] = t[(d[i] & 3) << 4]; q[2] = '='; }
        q[3] = '=';
        buf_add(b, q, 4);
    }
    buf_str(b, "\"");
}

/* ---------------- einheitliche Sicht auf A/W ---------------- */

typedef struct { ULONG cls; wchar_t *name; wchar_t *addr; } Recip;
typedef struct { wchar_t *path; wchar_t *name; } Att;
typedef struct { wchar_t *subject; wchar_t *body; ULONG nr; Recip *r; ULONG nf; Att *f; FLAGS flags; } Msg;

static void msg_free(Msg *m, int ownsStrings)
{
    if (ownsStrings)
    {
        free(m->subject); free(m->body);
        for (ULONG i = 0; i < m->nr; i++) { free(m->r[i].name); free(m->r[i].addr); }
        for (ULONG i = 0; i < m->nf; i++) { free(m->f[i].path); free(m->f[i].name); }
    }
    free(m->r); free(m->f);
}

static const wchar_t *base_name(const wchar_t *p)
{
    const wchar_t *s = p;
    for (const wchar_t *c = p; c && *c; c++) if (*c == L'\\' || *c == L'/') s = c + 1;
    return s;
}

static int read_file(const wchar_t *path, unsigned char **data, DWORD *size)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER li;
    if (!GetFileSizeEx(h, &li) || li.QuadPart > MAX_MESSAGE) { CloseHandle(h); return 0; }
    *size = (DWORD)li.QuadPart;
    *data = (unsigned char *)malloc(*size ? *size : 1);
    DWORD got = 0;
    BOOL ok = *data && (*size == 0 || ReadFile(h, *data, *size, &got, NULL));
    CloseHandle(h);
    if (!ok || got != *size) { free(*data); *data = NULL; return 0; }
    return 1;
}

/* Client-Computername der RDP-Sitzung */
static wchar_t *client_name(DWORD *sessionId)
{
    DWORD sid = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &sid);
    *sessionId = sid;
    LPWSTR name = NULL;
    DWORD bytes = 0;
    wchar_t *res = NULL;
    if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSClientName, &name, &bytes) && name && name[0])
    {
        res = _wcsdup(name);
    }
    if (name) WTSFreeMemory(name);
    if (!res)
    {
        wchar_t buf[MAX_COMPUTERNAME_LENGTH + 1];
        DWORD n = MAX_COMPUTERNAME_LENGTH + 1;
        if (GetComputerNameW(buf, &n)) res = _wcsdup(buf);
    }
    return res;
}

/* JSON für den Dienst; liefert 0 bei fehlendem Anhang (missing = Name) */
static int build_json(const Msg *m, Buf *b, wchar_t **missing)
{
    DWORD session = 0;
    wchar_t *client = client_name(&session);
    wchar_t exe[MAX_PATH] = L"";
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    char num[32];

    buf_str(b, "{\"version\":1,\"subject\":"); json_string_w(b, m->subject);
    buf_str(b, ",\"body\":"); json_string_w(b, m->body);
    buf_str(b, ",\"clientName\":"); json_string_w(b, client);
    sprintf(num, ",\"sessionId\":%lu", (unsigned long)session); buf_str(b, num);
    buf_str(b, ",\"process\":"); json_string_w(b, base_name(exe));
    buf_str(b, (m->flags & MAPI_DIALOG) ? ",\"dialog\":true" : ",\"dialog\":false");
    free(client);

    buf_str(b, ",\"recipients\":[");
    int first = 1;
    for (ULONG i = 0; i < m->nr; i++)
    {
        const Recip *r = &m->r[i];
        if (r->cls == MAPI_ORIG) continue;
        const wchar_t *addr = (r->addr && r->addr[0]) ? r->addr : r->name;
        if (!addr || !addr[0]) continue;
        if (!first) buf_str(b, ",");
        first = 0;
        buf_str(b, r->cls == MAPI_CC ? "{\"type\":\"cc\",\"address\":" : r->cls == MAPI_BCC ? "{\"type\":\"bcc\",\"address\":" : "{\"type\":\"to\",\"address\":");
        json_string_w(b, addr);
        buf_str(b, ",\"name\":"); json_string_w(b, r->name);
        buf_str(b, "}");
    }
    buf_str(b, "],\"attachments\":[");
    int firstAtt = 1;
    for (ULONG i = 0; i < m->nf; i++)
    {
        const Att *a = &m->f[i];
        if (!a->path || !a->path[0]) continue;
        unsigned char *data = NULL;
        DWORD size = 0;
        if (!read_file(a->path, &data, &size)) { *missing = (wchar_t *)a->path; return 0; }
        if (!firstAtt) buf_str(b, ",");
        firstAtt = 0;
        buf_str(b, "{\"name\":");
        json_string_w(b, (a->name && a->name[0]) ? a->name : base_name(a->path));
        buf_str(b, ",\"data\":");
        json_base64(b, data, size);
        buf_str(b, "}");
        free(data);
        if (b->len > MAX_MESSAGE) { b->failed = 1; return 1; }
    }
    buf_str(b, "]}");
    return 1;
}

/* ---------------- Ausgang (Dienst nicht erreichbar) ---------------- */

static int outbox_dir(wchar_t *dir)
{
    wchar_t base[MAX_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, base))) return 0;
    _snwprintf(dir, MAX_PATH, L"%ls\\Belegpost", base);
    CreateDirectoryW(dir, NULL);
    _snwprintf(dir, MAX_PATH, L"%ls\\Belegpost\\mapi-ausgang", base);
    CreateDirectoryW(dir, NULL);
    return 1;
}

static int outbox_save(const Buf *b)
{
    wchar_t dir[MAX_PATH], file[MAX_PATH];
    if (!outbox_dir(dir)) return 0;
    SYSTEMTIME t;
    GetLocalTime(&t);
    _snwprintf(file, MAX_PATH, L"%ls\\%04d%02d%02d-%02d%02d%02d-%03d-%lu.json", dir, t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds,
        (unsigned long)GetTickCount());
    HANDLE h = CreateFileW(file, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    DWORD w = 0;
    BOOL ok = WriteFile(h, b->p, (DWORD)b->len, &w, NULL) && w == b->len;
    CloseHandle(h);
    return ok;
}

/* ---------------- Pipe ---------------- */

/* 1 = angenommen (resp = Antwort-JSON), 0 = Dienst nicht erreichbar, -1 = Dienst hat abgelehnt */
static int pipe_send(const char *data, size_t len, char **resp)
{
    *resp = NULL;
    HANDLE h = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 3 && h == INVALID_HANDLE_VALUE; i++)
    {
        h = CreateFileW(PIPE_NAME, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
        if (h == INVALID_HANDLE_VALUE)
        {
            if (GetLastError() != ERROR_PIPE_BUSY) return 0;
            if (!WaitNamedPipeW(PIPE_NAME, 5000)) return 0;
        }
    }
    if (h == INVALID_HANDLE_VALUE) return 0;
    DWORD w = 0, n32 = (DWORD)len;
    BOOL ok = WriteFile(h, &n32, 4, &w, NULL) && w == 4;
    size_t off = 0;
    while (ok && off < len)
    {
        DWORD chunk = (DWORD)((len - off) > 65536 ? 65536 : (len - off));
        ok = WriteFile(h, data + off, chunk, &w, NULL) && w == chunk;
        off += w;
    }
    if (!ok) { CloseHandle(h); return 0; }
    DWORD rlen = 0, got = 0;
    if (!ReadFile(h, &rlen, 4, &got, NULL) || got != 4 || rlen > 65536) { CloseHandle(h); return 1; } /* gesendet, Antwort unklar → als angenommen werten */
    char *r = (char *)malloc(rlen + 1);
    DWORD total = 0;
    while (r && total < rlen && ReadFile(h, r + total, rlen - total, &got, NULL) && got > 0) total += got;
    CloseHandle(h);
    if (!r) return 1;
    r[total] = 0;
    *resp = r;
    return strstr(r, "\"ok\":false") ? -1 : 1;
}

/* abgelegte Nachrichten nachliefern */
static void outbox_flush(void)
{
    wchar_t dir[MAX_PATH], pattern[MAX_PATH], file[MAX_PATH];
    if (!outbox_dir(dir)) return;
    _snwprintf(pattern, MAX_PATH, L"%ls\\*.json", dir);
    WIN32_FIND_DATAW fd;
    HANDLE f = FindFirstFileW(pattern, &fd);
    if (f == INVALID_HANDLE_VALUE) return;
    do
    {
        _snwprintf(file, MAX_PATH, L"%ls\\%ls", dir, fd.cFileName);
        unsigned char *data = NULL;
        DWORD size = 0;
        if (!read_file(file, &data, &size)) continue;
        char *resp = NULL;
        int r = pipe_send((const char *)data, size, &resp);
        free(data); free(resp);
        if (r == 0) break;          /* Dienst weiterhin nicht erreichbar */
        DeleteFileW(file);          /* angenommen oder endgültig abgelehnt */
    } while (FindNextFileW(f, &fd));
    FindClose(f);
}

static void show(HWND owner, const wchar_t *text, UINT icon)
{
    MessageBoxW(owner, text, L"Belegpost", MB_OK | icon | MB_SETFOREGROUND);
}

/* Textfeld aus der Antwort ("message":"…", "hint":"…") für Anzeigen */
static wchar_t *resp_field(const char *resp, const char *key)
{
    char pat[32];
    _snprintf(pat, sizeof(pat), "\"%s\":\"", key);
    const char *p = resp ? strstr(resp, pat) : NULL;
    if (!p) return NULL;
    p += strlen(pat);
    char tmp[1024];
    size_t i = 0;
    while (*p && *p != '"' && i < sizeof(tmp) - 5)
    {
        if (*p != '\\' || !p[1]) { tmp[i++] = *p++; continue; }
        p++;
        char e = *p++;
        if (e == 'n') tmp[i++] = '\n';
        else if (e == 'r') tmp[i++] = '\r';
        else if (e == 't') tmp[i++] = '\t';
        else if (e == 'u' && isxdigit((unsigned char)p[0]) && isxdigit((unsigned char)p[1]) && isxdigit((unsigned char)p[2]) && isxdigit((unsigned char)p[3]))
        {
            /* \uXXXX als UTF-8 (BMP; Ersatzpaare werden als '?' ausgegeben) */
            char hex[5] = { p[0], p[1], p[2], p[3], 0 };
            unsigned cp = (unsigned)strtoul(hex, NULL, 16);
            p += 4;
            if (cp >= 0xD800 && cp <= 0xDFFF) tmp[i++] = '?';
            else if (cp < 0x80) tmp[i++] = (char)cp;
            else if (cp < 0x800) { tmp[i++] = (char)(0xC0 | (cp >> 6)); tmp[i++] = (char)(0x80 | (cp & 0x3F)); }
            else { tmp[i++] = (char)(0xE0 | (cp >> 12)); tmp[i++] = (char)(0x80 | ((cp >> 6) & 0x3F)); tmp[i++] = (char)(0x80 | (cp & 0x3F)); }
        }
        else tmp[i++] = e; /* \" \\ \/ */
    }
    tmp[i] = 0;
    int n = MultiByteToWideChar(CP_UTF8, 0, tmp, -1, NULL, 0);
    wchar_t *w = (wchar_t *)malloc(sizeof(wchar_t) * (n > 0 ? n : 1));
    if (w) MultiByteToWideChar(CP_UTF8, 0, tmp, -1, w, n);
    return w;
}

static ULONG send_msg(ULONG_PTR ui, const Msg *m)
{
    outbox_flush();
    Buf b = {0};
    wchar_t *missing = NULL;
    if (!build_json(m, &b, &missing))
    {
        wchar_t text[MAX_PATH + 128];
        _snwprintf(text, MAX_PATH + 128, L"Anhang nicht gefunden:\n%ls", missing ? missing : L"?");
        show((HWND)ui, text, MB_ICONERROR);
        free(b.p);
        return MAPI_E_ATTACHMENT_NOT_FOUND;
    }
    if (b.failed || b.len > MAX_MESSAGE)
    {
        show((HWND)ui, L"Die E-Mail ist zu groß für die Übergabe an Belegpost (höchstens 60 MB).", MB_ICONERROR);
        free(b.p);
        return MAPI_E_INSUFFICIENT_MEMORY;
    }
    char *resp = NULL;
    int r = pipe_send(b.p, b.len, &resp);
    ULONG rc = SUCCESS_SUCCESS;
    if (r == 0)
    {
        if (outbox_save(&b))
            show((HWND)ui, L"Belegpost ist gerade nicht erreichbar.\nDie E-Mail wurde zwischengespeichert und wird beim nächsten Versand automatisch übergeben.", MB_ICONWARNING);
        else { show((HWND)ui, L"Belegpost ist nicht erreichbar und die E-Mail konnte nicht zwischengespeichert werden.", MB_ICONERROR); rc = MAPI_E_FAILURE; }
    }
    else if (r < 0)
    {
        wchar_t *msg = resp_field(resp, "message");
        show((HWND)ui, msg ? msg : L"Belegpost konnte die E-Mail nicht annehmen.", MB_ICONERROR);
        free(msg);
        rc = MAPI_E_FAILURE;
    }
    else
    {
        /* angenommen – Hinweis anzeigen, wenn die Mail nicht dort landet, wo der Benutzer sie erwartet (z. B. im Portal statt in Outlook) */
        wchar_t *hint = resp_field(resp, "hint");
        if (hint && *hint) show((HWND)ui, hint, MB_ICONINFORMATION);
        free(hint);
    }
    free(resp);
    free(b.p);
    return rc;
}

/* ---------------- Exporte ---------------- */

EXPORT ULONG WINAPI MAPISendMailW(LHANDLE session, ULONG_PTR ui, MapiMessageW *msg, FLAGS flags, ULONG reserved)
{
    (void)session; (void)reserved;
    if (!msg) return MAPI_E_FAILURE;
    Msg m = {0};
    m.subject = msg->lpszSubject; m.body = msg->lpszNoteText; m.flags = flags;
    m.nr = msg->nRecipCount; m.nf = msg->nFileCount;
    m.r = (Recip *)calloc(m.nr + 1, sizeof(Recip));
    m.f = (Att *)calloc(m.nf + 1, sizeof(Att));
    if (!m.r || !m.f) { msg_free(&m, 0); return MAPI_E_INSUFFICIENT_MEMORY; }
    for (ULONG i = 0; i < m.nr; i++) { m.r[i].cls = msg->lpRecips[i].ulRecipClass; m.r[i].name = msg->lpRecips[i].lpszName; m.r[i].addr = msg->lpRecips[i].lpszAddress; }
    for (ULONG i = 0; i < m.nf; i++) { m.f[i].path = msg->lpFiles[i].lpszPathName; m.f[i].name = msg->lpFiles[i].lpszFileName; }
    ULONG rc = send_msg(ui, &m);
    msg_free(&m, 0);
    return rc;
}

EXPORT ULONG WINAPI MAPISendMail(LHANDLE session, ULONG_PTR ui, MapiMessageA *msg, FLAGS flags, ULONG reserved)
{
    (void)session; (void)reserved;
    if (!msg) return MAPI_E_FAILURE;
    Msg m = {0};
    m.subject = from_ansi(msg->lpszSubject); m.body = from_ansi(msg->lpszNoteText); m.flags = flags;
    m.nr = msg->nRecipCount; m.nf = msg->nFileCount;
    m.r = (Recip *)calloc(m.nr + 1, sizeof(Recip));
    m.f = (Att *)calloc(m.nf + 1, sizeof(Att));
    if (!m.r || !m.f) { msg_free(&m, 1); return MAPI_E_INSUFFICIENT_MEMORY; }
    for (ULONG i = 0; i < m.nr; i++)
    {
        m.r[i].cls = msg->lpRecips[i].ulRecipClass;
        m.r[i].name = from_ansi(msg->lpRecips[i].lpszName);
        m.r[i].addr = from_ansi(msg->lpRecips[i].lpszAddress);
    }
    for (ULONG i = 0; i < m.nf; i++) { m.f[i].path = from_ansi(msg->lpFiles[i].lpszPathName); m.f[i].name = from_ansi(msg->lpFiles[i].lpszFileName); }
    ULONG rc = send_msg(ui, &m);
    msg_free(&m, 1);
    return rc;
}

EXPORT ULONG WINAPI MAPILogon(ULONG_PTR ui, LPSTR profile, LPSTR password, FLAGS flags, ULONG reserved, LHANDLE *session)
{
    (void)ui; (void)profile; (void)password; (void)flags; (void)reserved;
    if (session) *session = 1;
    return SUCCESS_SUCCESS;
}

EXPORT ULONG WINAPI MAPILogoff(LHANDLE session, ULONG_PTR ui, FLAGS flags, ULONG reserved)
{
    (void)session; (void)ui; (void)flags; (void)reserved;
    return SUCCESS_SUCCESS;
}

/* MAPIResolveName: Adresse = Name (das ERP übergibt in der Regel bereits E-Mail-Adressen) */
EXPORT ULONG WINAPI MAPIResolveName(LHANDLE session, ULONG_PTR ui, LPSTR name, FLAGS flags, ULONG reserved, MapiRecipDescA **recip)
{
    (void)session; (void)ui; (void)flags; (void)reserved;
    if (!recip || !name) return MAPI_E_FAILURE;
    size_t n = strlen(name) + 1;
    MapiRecipDescA *r = (MapiRecipDescA *)GlobalAlloc(GPTR, sizeof(MapiRecipDescA) + 2 * n);
    if (!r) return MAPI_E_INSUFFICIENT_MEMORY;
    char *s = (char *)(r + 1);
    memcpy(s, name, n);
    memcpy(s + n, name, n);
    r->ulRecipClass = MAPI_TO; r->lpszName = s; r->lpszAddress = s + n;
    *recip = r;
    return SUCCESS_SUCCESS;
}

EXPORT ULONG WINAPI MAPIFreeBuffer(LPVOID p)
{
    if (p) GlobalFree(p);
    return SUCCESS_SUCCESS;
}

EXPORT ULONG WINAPI MAPISendDocuments(ULONG_PTR ui, LPSTR delim, LPSTR paths, LPSTR names, ULONG reserved)
{
    (void)ui; (void)delim; (void)paths; (void)names; (void)reserved;
    return MAPI_E_NOT_SUPPORTED;
}

EXPORT ULONG WINAPI MAPIFindNext(LHANDLE s, ULONG_PTR u, LPSTR t, LPSTR seed, FLAGS f, ULONG r, LPSTR id) { (void)s; (void)u; (void)t; (void)seed; (void)f; (void)r; (void)id; return MAPI_E_NOT_SUPPORTED; }
EXPORT ULONG WINAPI MAPIReadMail(LHANDLE s, ULONG_PTR u, LPSTR id, FLAGS f, ULONG r, LPVOID *m) { (void)s; (void)u; (void)id; (void)f; (void)r; (void)m; return MAPI_E_NOT_SUPPORTED; }
EXPORT ULONG WINAPI MAPISaveMail(LHANDLE s, ULONG_PTR u, LPVOID m, FLAGS f, ULONG r, LPSTR id) { (void)s; (void)u; (void)m; (void)f; (void)r; (void)id; return MAPI_E_NOT_SUPPORTED; }
EXPORT ULONG WINAPI MAPIDeleteMail(LHANDLE s, ULONG_PTR u, LPSTR id, FLAGS f, ULONG r) { (void)s; (void)u; (void)id; (void)f; (void)r; return MAPI_E_NOT_SUPPORTED; }
EXPORT ULONG WINAPI MAPIAddress(LHANDLE s, ULONG_PTR u, LPSTR c, ULONG e, LPSTR l, ULONG n, LPVOID r, FLAGS f, ULONG x, ULONG *nn, LPVOID *nr)
{ (void)s; (void)u; (void)c; (void)e; (void)l; (void)n; (void)r; (void)f; (void)x; (void)nn; (void)nr; return MAPI_E_NOT_SUPPORTED; }
EXPORT ULONG WINAPI MAPIDetails(LHANDLE s, ULONG_PTR u, LPVOID r, FLAGS f, ULONG x) { (void)s; (void)u; (void)r; (void)f; (void)x; return MAPI_E_NOT_SUPPORTED; }

/* ---------------- mailto:-Links (Windows-Standard-App „E-Mail“) ----------------
 * Aufruf über rundll32:  rundll32.exe "%SystemRoot%\System32\BelegpostMapi.dll",Mailto %1
 * mailto:a@b.at,c@d.at?subject=…&body=…&cc=…&bcc=…  (RFC 6068, Prozent-Kodierung UTF-8)
 */

static int hexval(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* n Bytes UTF-8 ab s prozent-dekodieren → neuer wchar_t-String */
static wchar_t *url_decode(const char *s, size_t n)
{
    char *t = (char *)malloc(n + 1);
    if (!t) return NULL;
    size_t j = 0;
    for (size_t i = 0; i < n; i++)
    {
        if (s[i] == '%' && i + 2 < n && hexval(s[i + 1]) >= 0 && hexval(s[i + 2]) >= 0)
        { t[j++] = (char)(hexval(s[i + 1]) * 16 + hexval(s[i + 2])); i += 2; }
        else t[j++] = s[i];
    }
    t[j] = 0;
    int wn = MultiByteToWideChar(CP_UTF8, 0, t, -1, NULL, 0);
    if (wn <= 0) wn = MultiByteToWideChar(CP_ACP, 0, t, -1, NULL, 0);
    wchar_t *w = (wchar_t *)malloc(sizeof(wchar_t) * (wn > 0 ? wn : 1));
    if (w)
    {
        if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, t, -1, w, wn)) MultiByteToWideChar(CP_ACP, 0, t, -1, w, wn);
    }
    free(t);
    return w;
}

/* Adressliste "a@b.at,c@d.at" (bereits dekodiert) als Empfänger anhängen */
static void add_recips(Msg *m, ULONG cls, const wchar_t *list)
{
    if (!list) return;
    const wchar_t *p = list;
    while (*p)
    {
        while (*p == L',' || *p == L';' || *p == L' ') p++;
        const wchar_t *e = p;
        while (*e && *e != L',' && *e != L';') e++;
        size_t len = (size_t)(e - p);
        while (len > 0 && p[len - 1] == L' ') len--;
        if (len > 0)
        {
            Recip *r = (Recip *)realloc(m->r, sizeof(Recip) * (m->nr + 2));
            if (!r) return;
            m->r = r;
            wchar_t *a = (wchar_t *)malloc(sizeof(wchar_t) * (len + 1));
            if (!a) return;
            memcpy(a, p, sizeof(wchar_t) * len); a[len] = 0;
            m->r[m->nr].cls = cls; m->r[m->nr].name = NULL; m->r[m->nr].addr = a;
            m->nr++;
        }
        p = e;
    }
}

EXPORT void CALLBACK MailtoW(HWND hwnd, HINSTANCE hinst, LPWSTR cmdLine, int show)
{
    (void)hinst; (void)show;
    Msg m = {0};
    m.r = (Recip *)calloc(1, sizeof(Recip));
    m.f = (Att *)calloc(1, sizeof(Att));
    char *u = to_utf8(cmdLine ? cmdLine : L"");
    if (!u || !m.r || !m.f) { free(u); msg_free(&m, 1); return; }

    /* Anführungszeichen, Leerzeichen und "mailto:" entfernen */
    char *s = u;
    while (*s == ' ' || *s == '"') s++;
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '"')) s[--n] = 0;
    if (_strnicmp(s, "mailto:", 7) == 0) s += 7;

    char *q = strchr(s, '?');
    size_t addrLen = q ? (size_t)(q - s) : strlen(s);
    wchar_t *to = url_decode(s, addrLen);
    add_recips(&m, MAPI_TO, to);
    free(to);

    for (char *part = q ? q + 1 : NULL; part && *part;)
    {
        char *amp = strchr(part, '&');
        size_t plen = amp ? (size_t)(amp - part) : strlen(part);
        char *eq = memchr(part, '=', plen);
        if (eq)
        {
            size_t klen = (size_t)(eq - part);
            wchar_t *val = url_decode(eq + 1, plen - klen - 1);
            if (val)
            {
                if (klen == 7 && _strnicmp(part, "subject", 7) == 0) { free(m.subject); m.subject = val; val = NULL; }
                else if (klen == 4 && _strnicmp(part, "body", 4) == 0) { free(m.body); m.body = val; val = NULL; }
                else if (klen == 2 && _strnicmp(part, "to", 2) == 0) add_recips(&m, MAPI_TO, val);
                else if (klen == 2 && _strnicmp(part, "cc", 2) == 0) add_recips(&m, MAPI_CC, val);
                else if (klen == 3 && _strnicmp(part, "bcc", 3) == 0) add_recips(&m, MAPI_BCC, val);
                free(val);
            }
        }
        part = amp ? amp + 1 : NULL;
    }
    free(u);
    send_msg((ULONG_PTR)hwnd, &m);
    msg_free(&m, 1);
}

EXPORT void CALLBACK Mailto(HWND hwnd, HINSTANCE hinst, LPSTR cmdLine, int show)
{
    wchar_t *w = from_ansi(cmdLine ? cmdLine : "");
    MailtoW(hwnd, hinst, w ? w : L"", show);
    free(w);
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
