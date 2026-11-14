using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BoSoFa.Server.Tests;

/// <summary>
/// SkillShare as BoSoFa sees it (docs/skillshare.md, "Wire contract"), on a loopback port: an OpenID provider with ES256 ID tokens,
/// refresh tokens that do not rotate and give tokens for one resource each, <c>client_credentials</c>, the connect API (skills,
/// archives, downloads, a person's MCP servers) and one MCP server per provider that accepts only tokens issued for its URL.
/// Its authorize endpoint signs in <see cref="NextPerson"/> at once and answers with the code as JSON, standing in for the browser.
/// </summary>
sealed partial class FakeSkillShare : IAsyncDisposable
{
    public const string ClientId = "app.bosofa-test", ClientSecret = "fake-client-secret-1234567890";

    public sealed record Person(string Sub, string Email, string Name, bool Admin = false, string Oid = "", string Tid = "tenant-1");

    public sealed record Skill(string Id, string Name, string Description, string Version, string Text, DateTime ChangedAt, bool Deleted = false);

    sealed record Code(Person Person, string Nonce, string Challenge, string Redirect, string Scope);

    sealed record Grant(Person Person, string Scope)
    {
        public bool Revoked { get; set; }
    }

    sealed record AccessToken(Person? Person, string Audience, string Scope, DateTime ExpiresAt);

    readonly WebApplication _app;
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly ConcurrentDictionary<string, Code> _codes = new();
    readonly ConcurrentDictionary<string, Grant> _grants = new();
    readonly ConcurrentDictionary<string, AccessToken> _tokens = new();

    /// <summary>Versions whose archive was asked for: as SkillShare does, a version's hash is known only from then on.</summary>
    readonly ConcurrentDictionary<string, bool> _archived = new();

    public Person NextPerson { get; set; } = new("sub-boris", "boris@example.com", "Boris", Admin: true, Oid: "oid-boris");
    public List<Skill> Skills { get; } = [];

    /// <summary>Per person (sub): their MCP servers, and whether they connected each.</summary>
    public ConcurrentDictionary<string, Dictionary<string, bool>> Mcps { get; } = new();

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public ConcurrentQueue<string> McpCalls { get; } = new();
    public ConcurrentQueue<string> Downloads { get; } = new();
    public ConcurrentQueue<string> TokenRequests { get; } = new();
    public ConcurrentQueue<string> RefreshTokensIssued { get; } = new();
    public string? LastAuthorizeScope { get; private set; }

    FakeSkillShare(WebApplication app) => _app = app;

    public string Origin => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
    public string McpUrl(string provider) => $"{Origin}/{provider}/mcp";

    public static async Task<FakeSkillShare> StartAsync(int port = 0)
    {
        var app = WebApplication.CreateSlimBuilder(["--urls", $"http://127.0.0.1:{port}"]).Build();
        var fake = new FakeSkillShare(app);
        app.MapGet("/.well-known/openid-configuration", fake.Discovery);
        app.MapGet("/.well-known/jwks.json", fake.Jwks);
        app.MapGet("/authorize", fake.Authorize);
        app.MapPost("/token", fake.TokenAsync);
        app.MapGet("/api/connect/v1/skills", fake.ListSkills);
        app.MapGet("/api/connect/v1/skills/{id}/versions/{version}/archive", fake.Archive);
        app.MapPost("/api/connect/v1/skills/{id}/versions/{version}/downloads", fake.Download);
        app.MapGet("/api/connect/v1/me/mcps", fake.ListMcps);
        app.MapPost("/{provider}/mcp", fake.McpAsync);
        await app.StartAsync();
        return fake;
    }

    /// <summary>What the server needs to sign in through this SkillShare.</summary>
    public string[] ServerArgs(params string[] more) =>
    [
        "--BoSoFa:Oidc:Authority", Origin, "--BoSoFa:Oidc:ClientId", ClientId, "--BoSoFa:Oidc:ClientSecret", ClientSecret,
        "--BoSoFa:Oidc:DisplayName", "SkillShare", ..more
    ];

    /// <summary>Access tokens issued so far stop working, as after their expiry; refresh tokens still work.</summary>
    public void ForgetAccessTokens() => _tokens.Clear();

    /// <summary>The person revoked the app: their refresh tokens and access tokens stop working.</summary>
    public void Revoke(string sub)
    {
        foreach (var grant in _grants.Values.Where(grant => grant.Person.Sub == sub)) grant.Revoked = true;
        foreach (var (token, issued) in _tokens) if (issued.Person?.Sub == sub) _tokens.TryRemove(token, out _);
    }

    IResult Discovery() => Results.Json(new JsonObject
    {
        ["issuer"] = Origin,
        ["authorization_endpoint"] = Origin + "/authorize",
        ["token_endpoint"] = Origin + "/token",
        ["jwks_uri"] = Origin + "/.well-known/jwks.json",
        ["response_types_supported"] = new JsonArray("code"),
        ["subject_types_supported"] = new JsonArray("public"),
        ["id_token_signing_alg_values_supported"] = new JsonArray("ES256"),
        ["scopes_supported"] = new JsonArray("openid", "profile", "email", "offline_access", "skills.read", "mcps.read", "mcp.use"),
        ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token", "client_credentials"),
        ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_basic", "client_secret_post"),
        ["skillshare_connect_api"] = Origin + "/api/connect/v1",
        ["skillshare_mcp_resources"] = new JsonArray(McpUrl("youtrack"), McpUrl("teams")),
        ["skillshare_name"] = "SkillShare"
    });

    IResult Jwks()
    {
        var parameters = _key.ExportParameters(false);
        return Results.Json(new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC", ["crv"] = "P-256", ["use"] = "sig", ["alg"] = "ES256", ["kid"] = "k1",
                ["x"] = Base64UrlEncoder.Encode(parameters.Q.X), ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y)
            })
        });
    }

    IResult Authorize(HttpRequest request)
    {
        var query = request.Query;
        if (query["client_id"] != ClientId || query["code_challenge_method"] != "S256" || string.IsNullOrEmpty(query["nonce"]))
            return Results.BadRequest("not a valid request");
        LastAuthorizeScope = query["scope"];
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = new(NextPerson, query["nonce"]!, query["code_challenge"]!, query["redirect_uri"]!, query["scope"]!);
        if (request.Headers.Accept.ToString().Contains("text/html"))
        {
            // A browser, when the fake runs by hand: the form post a real provider answers with.
            var html = $"""<html><body onload="document.forms[0].submit()"><form method="post" action="{WebUtility.HtmlEncode(query["redirect_uri"])}"><input type="hidden" name="code" value="{code}"><input type="hidden" name="state" value="{WebUtility.HtmlEncode(query["state"])}"></form></body></html>""";
            return Results.Content(html, "text/html");
        }
        return Results.Json(new JsonObject { ["redirect"] = query["redirect_uri"].ToString(), ["code"] = code, ["state"] = query["state"].ToString() });
    }

    async Task<IResult> TokenAsync(HttpRequest request)
    {
        var form = await request.ReadFormAsync();
        var basic = request.Headers.Authorization.ToString();
        var (id, secret) = basic.StartsWith("Basic ")
            ? Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])).Split(':', 2) is [var user, var password]
                ? (Uri.UnescapeDataString(user), Uri.UnescapeDataString(password)) : ("", "")
            : (form["client_id"].ToString(), form["client_secret"].ToString());
        if (id != ClientId || secret != ClientSecret) return Error("invalid_client", HttpStatusCode.Unauthorized);
        var grantType = form["grant_type"].ToString();
        TokenRequests.Enqueue($"{grantType} {form["resource"]} {form["scope"]}".Trim());
        switch (grantType)
        {
            case "authorization_code":
            {
                if (!_codes.TryRemove(form["code"].ToString(), out var code) || code.Redirect != form["redirect_uri"]) return Error("invalid_grant");
                var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
                if (challenge != code.Challenge) return Error("invalid_grant");
                var answer = new JsonObject
                {
                    ["access_token"] = Issue(code.Person, "skillshare-connect", code.Scope), ["token_type"] = "Bearer",
                    ["expires_in"] = (int)TokenLifetime.TotalSeconds, ["id_token"] = IdToken(code), ["scope"] = code.Scope
                };
                if (code.Scope.Split(' ').Contains("offline_access"))
                {
                    var refresh = "rt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                    _grants[refresh] = new(code.Person, code.Scope);
                    RefreshTokensIssued.Enqueue(refresh);
                    answer["refresh_token"] = refresh;
                }
                return Results.Json(answer);
            }
            case "refresh_token":
            {
                if (!_grants.TryGetValue(form["refresh_token"].ToString(), out var grant) || grant.Revoked) return Error("invalid_grant");
                var resource = form["resource"].ToString();
                if (resource.Length > 0 && !resource.StartsWith(Origin + "/")) return Error("invalid_target");
                var scope = form["scope"].ToString();
                return Results.Json(new JsonObject
                {
                    ["access_token"] = Issue(grant.Person, resource.Length > 0 ? resource : "skillshare-connect", scope), ["token_type"] = "Bearer",
                    ["expires_in"] = (int)TokenLifetime.TotalSeconds
                });
            }
            case "client_credentials":
                return form["scope"] == "skills.read"
                    ? Results.Json(new JsonObject
                    {
                        ["access_token"] = Issue(null, "skillshare-connect", "skills.read"), ["token_type"] = "Bearer", ["expires_in"] = (int)TokenLifetime.TotalSeconds
                    })
                    : Error("invalid_scope");
            default:
                return Error("unsupported_grant_type");
        }
    }

    static IResult Error(string error, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        Results.Json(new JsonObject { ["error"] = error, ["error_description"] = error.Replace('_', ' ') }, statusCode: (int)status);

    string Issue(Person? person, string audience, string scope)
    {
        var token = "at-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = new(person, audience, scope, DateTime.UtcNow + TokenLifetime);
        return token;
    }

    string IdToken(Code code)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = code.Person.Sub, ["email"] = code.Person.Email, ["name"] = code.Person.Name, ["nonce"] = code.Nonce,
            ["skillshare_admin"] = code.Person.Admin, ["tid"] = code.Person.Tid
        };
        if (code.Person.Oid.Length > 0) claims["oid"] = code.Person.Oid;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Origin, Audience = ClientId, Claims = claims, IssuedAt = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = "k1" }, SecurityAlgorithms.EcdsaSha256)
        });
    }

    AccessToken? Bearer(HttpRequest request, string audience)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ") && _tokens.TryGetValue(header[7..], out var token) && token.Audience == audience && token.ExpiresAt > DateTime.UtcNow
            ? token
            : null;
    }

    IResult ListSkills(HttpRequest request, string? changedSince)
    {
        if (Bearer(request, "skillshare-connect") is not { } token || !token.Scope.Contains("skills.read")) return Results.Unauthorized();
        var since = changedSince == null ? DateTime.MinValue : DateTime.Parse(changedSince, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var skills = new JsonArray();
        foreach (var skill in Skills.GroupBy(skill => skill.Id).Where(versions => versions.Any(version => version.ChangedAt > since)))
        {
            var latest = skill.Last();
            skills.Add(new JsonObject
            {
                ["id"] = latest.Id, ["name"] = latest.Name, ["description"] = latest.Description, ["tags"] = new JsonArray("ops"), ["owner"] = "Alice",
                ["deprecated"] = false, ["deleted"] = latest.Deleted,
                ["versions"] = new JsonArray(skill.Select(version => (JsonNode)new JsonObject
                {
                    ["version"] = version.Version, ["publishedAt"] = version.ChangedAt.ToString("O"), ["latest"] = version == latest,
                    ["contentSha256"] = _archived.ContainsKey($"{version.Id}@{version.Version}") ? Convert.ToHexStringLower(SHA256.HashData(Zip(version.Text))) : null
                }).ToArray())
            });
        }
        return Results.Json(new JsonObject { ["serverTime"] = DateTimeOffset.UtcNow.ToString("O"), ["skills"] = skills });
    }

    IResult Archive(HttpRequest request, string id, string version)
    {
        if (Bearer(request, "skillshare-connect") is null) return Results.Unauthorized();
        if (Skills.FirstOrDefault(skill => skill.Id == id && skill.Version == version) is not { } found) return Results.NotFound();
        _archived[$"{id}@{version}"] = true;
        return Results.Bytes(Zip(found.Text), "application/zip");
    }

    IResult Download(HttpRequest request, string id, string version)
    {
        if (Bearer(request, "skillshare-connect") is not { Person: { } person }) return Results.Unauthorized();
        Downloads.Enqueue($"{person.Email} {id} {version}");
        return Results.NoContent();
    }

    IResult ListMcps(HttpRequest request)
    {
        if (Bearer(request, "skillshare-connect") is not { Person: { } person } token || !token.Scope.Contains("mcps.read")) return Results.Unauthorized();
        var servers = Mcps.GetValueOrDefault(person.Sub) ?? [];
        return Results.Json(new JsonObject
        {
            ["servers"] = new JsonArray(servers.Select(server => (JsonNode)new JsonObject
            {
                ["id"] = server.Key, ["title"] = server.Key, ["url"] = McpUrl(server.Key), ["connected"] = server.Value, ["accessMode"] = "readOnly",
                ["setupUrl"] = Origin + "/mcps"
            }).ToArray())
        });
    }

    async Task<IResult> McpAsync(HttpRequest request, string provider)
    {
        if (Bearer(request, McpUrl(provider)) is not { Person: { } person } token || !token.Scope.Contains("mcp.use")) return Results.Unauthorized();
        var message = await JsonNode.ParseAsync(request.Body) as JsonObject;
        var method = message?["method"]?.GetValue<string>();
        if (message?["id"] is not { } id) return Results.StatusCode(202);
        McpCalls.Enqueue($"{provider} {method} {person.Email}");
        JsonNode result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = provider, ["version"] = "1" }
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "search_issues", ["description"] = "Finds issues.", ["inputSchema"] = new JsonObject { ["type"] = "object" }
                })
            },
            "tools/call" => new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"The issues of {person.Email}" })
            },
            _ => new JsonObject()
        };
        return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
    }

    public static byte[] Zip(string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("SKILL.md");
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
        return output.ToArray();
    }

    /// <summary>Signs <see cref="NextPerson"/> in to the server as a browser would, and returns a client with their cookie.</summary>
    public async Task<HttpClient> SignInAsync(TestServer server, Person? person = null)
    {
        if (person != null) NextPerson = person;
        var jar = new CookieJar(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var client = new HttpClient(jar) { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        using var login = await client.GetAsync("/auth/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var plain = new HttpClient();
        var authorized = JsonNode.Parse(await plain.GetStringAsync(login.Headers.Location))!;
        using var callback = await client.PostAsync(authorized["redirect"]!.GetValue<string>(), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = authorized["code"]!.GetValue<string>(), ["state"] = authorized["state"]!.GetValue<string>()
        }));
        Assert.True(callback.StatusCode == HttpStatusCode.Redirect, $"The sign-in callback answered {(int)callback.StatusCode}: {await callback.Content.ReadAsStringAsync()}");
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _key.Dispose();
    }

    /// <summary>Cookies by name for one server, Secure ones too: ASP.NET marks its correlation cookies Secure even over the test's plain HTTP.</summary>
    sealed partial class CookieJar(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        readonly ConcurrentDictionary<string, string> _cookies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_cookies.IsEmpty) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies)
                {
                    var pair = cookie.Split(';', 2)[0].Split('=', 2);
                    if (pair is not [var name, var value]) continue;
                    if (value.Length == 0 || Expired().IsMatch(cookie)) _cookies.TryRemove(name, out _);
                    else _cookies[name] = value;
                }
            return response;
        }

        [GeneratedRegex("expires=Thu, 01 Jan 1970", RegexOptions.IgnoreCase)]
        private static partial Regex Expired();
    }
}
