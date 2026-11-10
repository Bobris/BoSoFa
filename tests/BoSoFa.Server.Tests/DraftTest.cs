using System.Net;
using System.Net.Http.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class DraftTest
{
    static async Task<HttpClient> SignIn(TestServer server, string email)
    {
        var client = server.Client();
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task ADraftIsSavedAsItIsWrittenSeenOnlyByItsWriterAndBecomesTheSession()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com");
        using var anna = await SignIn(server, "anna@example.com");

        var created = await boris.PostAsJsonAsync("/api/v1/drafts", new SaveSessionDraftRequest("", """{"harness":"codex"}"""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<SessionDraftDto>())!;
        Assert.Equal(("", """{"harness":"codex"}"""), (draft.Title, draft.Settings));

        var saved = await boris.PutAsJsonAsync($"/api/v1/drafts/{draft.Id}", new SaveSessionDraftRequest("Fix the flaky test\nin CI", """{"harness":"claude"}"""));
        var updated = (await saved.Content.ReadFromJsonAsync<SessionDraftDto>())!;
        Assert.Equal((draft.Id, "Fix the flaky test", """{"harness":"claude"}"""), (updated.Id, updated.Title, updated.Settings));
        Assert.True(updated.UpdatedAt >= draft.UpdatedAt);
        Assert.Equal(draft.Id, Assert.Single((await boris.GetFromJsonAsync<SessionDraftDto[]>("/api/v1/drafts"))!).Id);

        // Nobody else sees, changes or discards it.
        Assert.Empty((await anna.GetFromJsonAsync<SessionDraftDto[]>("/api/v1/drafts"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"/api/v1/drafts/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anna.PutAsJsonAsync($"/api/v1/drafts/{draft.Id}", new SaveSessionDraftRequest("mine", "{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.DeleteAsync($"/api/v1/drafts/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boris.PutAsJsonAsync($"/api/v1/drafts/{draft.Id}", new SaveSessionDraftRequest("", new string('x', 20_000)))).StatusCode);

        // Starting the session takes the draft away.
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, Path.Combine(server.DataDirectory, "daemon"));
        var session = await boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(state.MachineId, Harnesses.Replay,
            server.DataDirectory, updated.Prompt, null, null, DraftId: draft.Id));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Empty((await boris.GetFromJsonAsync<SessionDraftDto[]>("/api/v1/drafts"))!);

        // Another one is discarded.
        var other = (await (await boris.PostAsJsonAsync("/api/v1/drafts", new SaveSessionDraftRequest("Later", "{}"))).Content
            .ReadFromJsonAsync<SessionDraftDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync($"/api/v1/drafts/{other.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await boris.GetAsync($"/api/v1/drafts/{other.Id}")).StatusCode);
    }
}
