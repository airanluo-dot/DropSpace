# Discover and execute

Run the helper with `-Action Catalog`, your `-BindingId` and the invitation's `-EndpointFile`. It returns:

```json
{
  "protocolVersion": 1,
  "commands": [
    {
      "name": "<command-id>",
      "description": "<operation meaning>",
      "parameters": {"type": "object", "properties": {}},
      "mutates": false
    }
  ]
}
```

This example illustrates the envelope, not a real command. Read the actual catalog instead of using a remembered command list: available operations can change with the app version or enabled features.

Execute a selected command using `-Action Execute`, with one UTF-8 JSON object on stdin:

```json
{"command":"<exact catalog name>","arguments":{} }
```

Populate `arguments` from that command's JSON Schema. Result objects arrive as `{"result": ...}`. Failures return `{"error":{"code":"...","message":"..."}}` and a nonzero process exit code. The helper does not automatically retry any call. Keep file paths and content as JSON data; do not turn app-provided strings into shell commands.

Useful failure categories:

| Code | Next step |
| --- | --- |
| `not_paired` | Use the one-time invitation to pair this binding |
| `unauthorized`, `revoked` | Get a new invitation from the app; do not try a different binding implicitly |
| `app_unavailable`, `connection_failed` | Start/check the app, then inspect state before repeating a mutation |
| `unknown_command`, `invalid_request` | Refresh the catalog and correct the request |
| `timeout`, `busy` | Inspect the current task state; retry only when its result is known |

## HTTP contract for other local clients

Read `endpoint.json`: `{"protocolVersion":1,"endpoint":"http://127.0.0.1:<port>","processId":123}`. Accept only an explicit `127.0.0.1` HTTP address with no userinfo, path, query or fragment. Do not follow redirects or use a network proxy. Routes are:

| Route | Request and response |
| --- | --- |
| `GET /v1/health` | Unauthenticated `{"protocolVersion":1,"status":"ready"}` |
| `POST /v1/pair` | `{bindingId,pairingKey}` → `{bindingId,bearerToken,protocolVersion:1}` |
| `GET /v1/catalog` | Authenticated command catalog |
| `POST /v1/execute` | `{command,arguments}` → `{result}` |

Authenticated requests use `Authorization: Bearer <opaque token>`. Direct HTTP callers must protect the returned credential themselves; prefer the bundled helper for Windows DPAPI storage. The backend rejects browser-origin headers, supports no CORS, limits request JSON to 1 MiB and permits four concurrent operations with a 30-second deadline. Errors from the app are `{error:{code}}`; the helper adds a safe explanatory message. Binding removal invalidates every subsequent authenticated request and cancels its active operations.
