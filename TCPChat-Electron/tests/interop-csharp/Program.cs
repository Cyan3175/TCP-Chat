using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TCPChat10.Models;
using TCPChat10.Services;

// File-driven interop harness: runs one batch of operations described in a JSON
// request file and writes the results to a response file. File-based rather than
// stdio so it can be driven from any runner without pipe plumbing.
//
//   dotnet run -- <request.json> <response.json>

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: interop <request.json> <response.json>");
    return 2;
}

var request = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
var ops = request["ops"]!.AsArray();
var results = new JsonArray();

foreach (var node in ops)
{
    var op = node!.AsObject();
    var kind = op["op"]!.GetValue<string>();
    var result = new JsonObject { ["op"] = kind };

    try
    {
        var password = op["password"]?.GetValue<string>() ?? "";
        var saltSeed = op["saltSeed"]?.GetValue<string>() ?? "";
        var aad = op["aad"]?.GetValue<string>() ?? "";

        switch (kind)
        {
            case "key":
            {
                // Raw PBKDF2 output, so the derivation itself can be compared.
                var key = MessageCrypto.DeriveKey(password, saltSeed);
                result["key"] = Convert.ToHexString(key).ToLowerInvariant();
                break;
            }

            case "enc":
            {
                var key = MessageCrypto.DeriveKey(password, saltSeed);
                result["envelope"] = MessageCrypto.Encrypt(key, aad, op["text"]!.GetValue<string>());
                break;
            }

            case "dec":
            {
                var key = MessageCrypto.DeriveKey(password, saltSeed);
                var plain = MessageCrypto.TryDecrypt(key, aad, op["envelope"]!.GetValue<string>());
                result["text"] = plain is null ? null : JsonValue.Create(plain);
                break;
            }

            case "encb":
            {
                var key = MessageCrypto.DeriveKey(password, saltSeed);
                var plain = Convert.FromBase64String(op["b64"]!.GetValue<string>());
                result["b64"] = Convert.ToBase64String(MessageCrypto.EncryptBytes(key, aad, plain));
                break;
            }

            case "decb":
            {
                var key = MessageCrypto.DeriveKey(password, saltSeed);
                var blob = Convert.FromBase64String(op["b64"]!.GetValue<string>());
                var plain = MessageCrypto.TryDecryptBytes(key, aad, blob);
                result["b64"] = plain is null ? null : JsonValue.Create(Convert.ToBase64String(plain));
                break;
            }

            case "payload":
            {
                // Full MessageCipher path: the JSON the app actually puts in `enc`.
                var cipher = new MessageCipher(password, saltSeed);
                Attachment? attach = null;
                if (op["attach"] is JsonObject a)
                {
                    attach = new Attachment
                    {
                        Name = a["name"]?.GetValue<string>() ?? "",
                        Path = a["path"]?.GetValue<string>() ?? "",
                        Size = a["size"]?.GetValue<long>() ?? 0,
                        Kind = a["kind"]?.GetValue<int>() ?? 1,
                        DurationMs = a["dur"]?.GetValue<int>() ?? 0,
                    };
                }
                var envelope = cipher.EncryptPayload(
                    aad,
                    op["from"]?.GetValue<string>() ?? "",
                    op["text"]?.GetValue<string>() ?? "",
                    op["quote"]?.GetValue<string>(),
                    attach);
                result["envelope"] = envelope is null ? null : JsonValue.Create(envelope);
                break;
            }

            case "unpayload":
            {
                // Decrypt with a password list + send index, exactly as ChatService does.
                var passwords = op["passwords"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
                var sendIndex = op["sendIndex"]?.GetValue<int>() ?? 0;
                var cipher = new MessageCipher(passwords, sendIndex, saltSeed);
                var payload = cipher.TryDecryptPayload(aad, op["envelope"]!.GetValue<string>());
                if (payload is null)
                {
                    result["payload"] = null;
                }
                else
                {
                    var o = new JsonObject
                    {
                        ["from"] = payload.From,
                        ["text"] = payload.Text,
                    };
                    if (payload.Quote is not null) o["quote"] = payload.Quote;
                    if (payload.Attach is not null)
                    {
                        o["attach"] = new JsonObject
                        {
                            ["name"] = payload.Attach.Name,
                            ["path"] = payload.Attach.Path,
                            ["size"] = payload.Attach.Size,
                            ["kind"] = payload.Attach.Kind,
                            ["dur"] = payload.Attach.DurationMs,
                        };
                    }
                    result["payload"] = o;
                }
                break;
            }

            default:
                result["error"] = "unknown op: " + kind;
                break;
        }
    }
    catch (Exception ex)
    {
        result["error"] = ex.GetType().Name + ": " + ex.Message;
    }

    results.Add(result);
}

var response = new JsonObject { ["results"] = results };
File.WriteAllText(args[1], response.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
return 0;
