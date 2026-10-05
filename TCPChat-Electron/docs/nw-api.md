# nw.zhaohans.cn — reverse-engineered HTTP API

Notes from probing the service on 2025-10-05, taken while looking for a second
WebDAV endpoint to fall back to. **It is not WebDAV.** It is a file-browsing web
UI on top of what appears to be the same storage the WebDAV server exposes: the
`msg_*.json` files written by the app through WebDAV are listed and readable
through this API, unchanged.

Everything below was verified against the live host. Anything unverified is
marked as such — do not treat this file as complete.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/nw/auth` | Authenticate, returns a session cookie |
| `GET` | `/nw/?dir=<path>` | HTML listing of a directory; also sets the session's current directory |
| `GET` | `/nw/download?file=<path>` | Read one file |
| `POST` | `/nw/upload` | Write one file (multipart) |
| `GET` | `/nw/api/folders-public` | JSON tree of every folder |
| `GET` | `/nw/setting` | Settings page |

There is no delete endpoint. See "What is missing" below.

## Authentication

```http
POST /nw/auth
Content-Type: application/json

{"password": "<password>", "isSuperAuth": false}
```

```http
200 OK
Set-Cookie: session_id=<id>; Path=/; HttpOnly; Max-Age=86400

{"success":true}
```

- The session lasts 24 hours; a request after that needs a fresh `auth`.
- `isSuperAuth: true` is a **different, more privileged** password. The ordinary
  password with that flag set answers `401 {"success":false,"error":"超级管理员密码错误"}`.
  Nothing here needs it.
- The `session_id` cookie is `HttpOnly`, so it must be carried explicitly rather
  than read from a page.

## Listing a directory

There is **no JSON file-listing endpoint**. `/nw/api/folders-public` returns the
folder tree only — no files. Files come from the HTML page:

```http
GET /nw/?dir=nw%E9%9B%86%E8%AE%AD%2F... (forward slashes, URL-encoded)
```

Two reasons this request matters beyond the HTML:

1. It lists the files, one per row, each carrying its full path:
   ```html
   <div class="file-row" onclick="showAuthModal('nw集训\学生资料临存\tcp_chat\msg_1791025225831_8f1071fc.json')">
   ```
   Note the **backslashes** — the page hands out Windows-style paths.
2. It sets the session's current directory, which `download` appears to depend on.

## Reading a file

```http
GET /nw/download?file=<full path, backslashes, URL-encoded>
Cookie: session_id=<id>
```

```http
200 OK
{"v":2,"id":"1791025225831_8f1071fc","from":"WZJ","time":"2026-10-03T11:00:25.831Z","text":"","enc":"AESGCM1:..."}
```

**The `file` parameter is the full backslash path, not the file name.** Passing
just the name returns `403` with an HTML body, which is what made this look
unreachable at first — the page's own links use the full path, so the short form
was never exercised by the UI.

## Writing a file

From the page's upload handler:

```js
const form = new FormData()
form.append('password', password)
// ...files
await fetch('/nw/upload', { method: 'POST', body: form })
```

Confirmed by the operator: **this overwrites a file of the same name**, and it
uses the *upload* password, which is a separate field from the download auth.

**Unverified.** No upload has been attempted, deliberately: nothing here can be
deleted, so any test file would stay in the storage permanently. Verifying this
costs one file that cannot be cleaned up.

## What is missing

**There is no delete.** The operator confirms the UI cannot remove a file and no
endpoint for it was found.

This is the one thing that matters for using this API as a transport. The app's
withdraw deletes the message file and its attachment:

```js
const ok = await this.dav.remove(combine(this.chatFolder, msg.remoteName))
if (!ok) return false
this._deleted.add(msg.remoteName)   // local bookkeeping only
```

The local `_deleted` set only stops *this* client from listing the message again.
The other side would still see it. So on this transport, withdraw genuinely
cannot work — it is not a matter of wiring.

The alternative would be to overwrite the message with a tombstone record, which
upload permits. That changes the wire format, and the C# build and any older
client would have to understand it. Not worth it for a fallback path.

## Why this is a second transport, not a fallback

The WebDAV client speaks PROPFIND / GET / PUT / DELETE / MKCOL with XML
multistatus bodies. This API speaks JSON and HTML with a session cookie, has no
delete, and identifies files by backslash path. Nothing is shared between them
beyond the bytes of the files themselves.

A useful side effect of that: because both front doors reach the same storage,
a client on this transport and a client on WebDAV read and write the same
messages and interoperate, as long as no withdraw is involved.
