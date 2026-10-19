using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class AgentSessionTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeProvider _provider = new();
    private readonly FakeApprover _approver = new();
    private readonly List<AgentEvent> _events = [];

    public void Dispose() => _folder.Dispose();

    private AgentSession Session(int maxSteps = 30)
    {
        var session = new AgentSession(
            _provider, new AgentWorkspace(_folder.Path), _approver, new AgentOptions("vendor/model", true, ReasoningEffort.Medium, maxSteps));
        session.Progress += (_, e) => _events.Add(e);
        return session;
    }

    private static ToolCallDelta Call(string id, string name, string arguments, int index = 0) => new(index, id, name, arguments);

    /// <summary>요청마다 모든 도구 호출에 짝이 되는 tool 메시지가 있어야 API가 받아준다.</summary>
    private static void AssertEveryToolCallAnswered(IReadOnlyList<ChatMessage> messages)
    {
        var calls = messages.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id).ToList();
        var answers = messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId!).ToList();
        Assert.Equal(calls, answers);
    }

    [Fact]
    public async Task Run_ExecutesToolCall_AndSendsResultBack_UntilModelAnswers()
    {
        _folder.File("rect.m", "A = w*h;\n");
        _provider
            .Reply(new TextDelta("파일을 볼게요."), Call("call_1", "read_file", "{\"path\":"), new ToolCallDelta(0, null, null, "\"rect.m\"}"), new Completed())
            .Reply(new TextDelta("문제 없습니다."), new Completed());
        var session = Session();

        await session.RunAsync("rect.m 검수해줘");

        Assert.Equal(AgentStatus.Done, session.Status);
        Assert.Equal(2, _provider.Requests.Count);
        Assert.All(_provider.Requests, r => Assert.Same(AgentTools.Definitions, r.Tools));

        var second = _provider.Requests[1].Messages;
        Assert.Equal(ChatRole.System, second[0].Role);
        Assert.Contains(_folder.Path, second[0].Content);
        // 필요한 새 함수 파일은 사용자에게 미루지 말고 직접 만들게 한다 (실사용에서 "직접 만드세요"로 끝난 사례).
        Assert.Contains("never tell the user to create files you can create", second[0].Content);
        Assert.Equal(new ChatMessage(ChatRole.User, "rect.m 검수해줘"), second[1]);
        Assert.Equal("파일을 볼게요.", second[2].Content);
        Assert.Equal([new ToolCall("call_1", "read_file", "{\"path\":\"rect.m\"}")], second[2].ToolCalls!);
        Assert.Equal(new ChatMessage(ChatRole.Tool, "A = w*h;\n", ToolCallId: "call_1"), second[3]);

        Assert.Equal("문제 없습니다.", session.Messages[^1].Content);
        Assert.Contains(_events, e => e is AgentToolStarted { Call.Name: "read_file" });
        Assert.Contains(_events, e => e is AgentToolFinished { Result.IsError: false });
        Assert.Equal(new AgentStatusChanged(AgentStatus.Done, null), _events[^1]);
    }

    [Fact]
    public async Task Run_SeveralCallsInOneReply_RunInOrder_AndMissingIdsAreFilled()
    {
        _provider
            .Reply(
                new ToolCallDelta(0, null, "write_file", """{"path":"a.m","content":"a = 1;"}"""),
                new ToolCallDelta(1, null, "write_file", """{"path":"b.m","content":"b = 2;"}"""),
                new Completed())
            .Reply(new TextDelta("만들었습니다."), new Completed());
        var session = Session();

        await session.RunAsync("a.m, b.m 만들어줘");

        Assert.Equal(["a.m", "b.m"], _approver.Changes.Select(c => c.Path));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "b.m")));
        var calls = _provider.Requests[1].Messages[2].ToolCalls!;
        Assert.Equal(["call_0_0", "call_0_1"], calls.Select(c => c.Id));
        AssertEveryToolCallAnswered(_provider.Requests[1].Messages);
    }

    [Fact]
    public async Task Run_ToolError_IsSentToModel_NotThrown()
    {
        _provider
            .Reply(Call("call_1", "read_file", """{"path":"../secret.m"}"""), new Completed())
            .Reply(new TextDelta("작업 폴더 밖이라 읽을 수 없습니다."), new Completed());
        var session = Session();

        await session.RunAsync("상위 폴더 파일 읽어");

        Assert.Equal(AgentStatus.Done, session.Status);
        var toolMessage = _provider.Requests[1].Messages[^1];
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        Assert.Contains("outside the workspace", toolMessage.Content);
    }

    [Fact]
    public async Task Run_StopsAtStepLimit_AndEmptyInstructionContinues()
    {
        _provider
            .Reply(Call("c1", "list_files", "{}"), new Completed())
            .Reply(Call("c2", "list_files", "{}"), new Completed())
            .Reply(new TextDelta("끝"), new Completed());
        var session = Session(maxSteps: 2);

        await session.RunAsync("모든 함수 검수");

        Assert.Equal(AgentStatus.Failed, session.Status);
        Assert.Equal(AgentSession.StepLimitMessage, session.Error!.Message);

        await session.RunAsync("");

        Assert.Equal(AgentStatus.Done, session.Status);
        var third = _provider.Requests[2].Messages;
        Assert.Single(third, m => m.Role == ChatRole.User);
        AssertEveryToolCallAnswered(third);
    }

    [Fact]
    public async Task Cancel_WhileWaitingForApproval_LeavesFileAndKeepsConversationValid()
    {
        _folder.File("a.m", "x = 1;\n");
        _approver.Hang = true;
        _provider
            .Reply(
                Call("c1", "edit_file", """{"path":"a.m","old_text":"x = 1;","new_text":"x = 2;"}"""),
                Call("c2", "read_file", """{"path":"a.m"}""", index: 1),
                new Completed())
            .Reply(new TextDelta("알겠습니다."), new Completed());
        var session = Session();

        var run = session.RunAsync("x를 2로");
        while (_approver.Changes.Count == 0) await Task.Delay(10);
        session.Cancel();
        await run;

        Assert.Equal(AgentStatus.Cancelled, session.Status);
        Assert.Equal("x = 1;\n", File.ReadAllText(Path.Combine(_folder.Path, "a.m")));

        _approver.Hang = false;
        await session.RunAsync("그냥 두자");

        AssertEveryToolCallAnswered(_provider.Requests[1].Messages);
        Assert.Equal(AgentStatus.Done, session.Status);
    }

    [Fact]
    public async Task Run_ProviderThrows_FailsWithUserError()
    {
        _provider.Throw(new NetworkException(new HttpRequestException()));
        var session = Session();

        await session.RunAsync("검수");

        Assert.Equal(AgentStatus.Failed, session.Status);
        Assert.Equal(ErrorMessages.For(new NetworkException(new HttpRequestException())), session.Error);
    }

    [Fact]
    public async Task Run_StreamFailure_FailsWithoutRecordingPartialReply()
    {
        _provider.Reply(new TextDelta("부분"), new StreamFailed("overloaded"));
        var session = Session();

        await session.RunAsync("검수");

        Assert.Equal(AgentStatus.Failed, session.Status);
        Assert.Contains("overloaded", session.Error!.Message);
        Assert.Equal(ChatRole.User, session.Messages[^1].Role);
    }

    [Fact]
    public async Task Run_FollowUpInstruction_KeepsHistory()
    {
        _provider
            .Reply(new TextDelta("첫 답"), new Completed())
            .Reply(new TextDelta("둘째 답"), new Completed());
        var session = Session();

        await session.RunAsync("첫 지시");
        await session.RunAsync("둘째 지시");

        Assert.Equal(
            ["첫 지시", "첫 답", "둘째 지시"],
            _provider.Requests[1].Messages.Skip(1).Select(m => m.Content));
    }

    [Fact]
    public async Task Run_EmptyInstructionBeforeAnything_DoesNothing()
    {
        var session = Session();

        await session.RunAsync("  ");

        Assert.Empty(_provider.Requests);
        Assert.Equal(AgentStatus.Ready, session.Status);
    }

    [Fact]
    public async Task SystemPrompt_RenamesFunctionsInPlace_WithoutWrapperFiles()
    {
        _provider.Reply(new TextDelta("완료"), new Completed());

        await Session().RunAsync("함수 이름 바꿔줘");

        // 실사용에서 이름만 바꾸라는 지시에 새 파일 + 호환 래퍼를 만든 사례.
        var system = _provider.Requests[0].Messages[0].Content;
        Assert.Contains("Renaming a function means changing its name (and its callers) in place", system);
        Assert.Contains("unless the user asks for a new function file", system);
    }

    [Fact]
    public async Task Run_WithLog_RecordsInstructionToolsAndReply_WithoutCode()
    {
        using var logs = new TempFolder();
        _folder.File("rect.m", "A = w*h;\n");
        _provider
            .Reply(Call("call_1", "read_file", "{\"path\":\"rect.m\"}"), new Completed())
            .Reply(Call("call_2", "edit_file", "{\"path\":\"rect.m\",\"old_text\":\"없는 텍스트\",\"new_text\":\"B = 1;\"}"), new Completed())
            // 실패 메시지에 가장 비슷한 줄(파일 내용)이 붙는 경우.
            .Reply(Call("call_3", "edit_file", "{\"path\":\"rect.m\",\"old_text\":\"A = w*h;\\nC = 2;\",\"new_text\":\"B = 1;\"}"), new Completed())
            .Reply(Call("call_4", "search_files", "{\"query\":\"rect\",\"path\":\"\"}"), new Completed())
            .Reply(new TextDelta("이름을 바꿨습니다."), new Completed());
        var log = new AgentLog(_folder.Path, logs.Path, new DateTimeOffset(2026, 10, 19, 21, 45, 41, TimeSpan.FromHours(9)));
        var session = new AgentSession(
            _provider, new AgentWorkspace(_folder.Path), _approver, new AgentOptions("vendor/model", true, ReasoningEffort.Medium), log: log);

        await session.RunAsync("이름 바꿔줘\nSelected code:\n```\nSECRET_CODE = 1;\nSECRET_TWO = 2;\n```\nThis runs without a window.");

        Assert.Equal(logs.Path, Path.GetDirectoryName(log.FilePath));
        Assert.StartsWith("20261019-214541 ", Path.GetFileName(log.FilePath));
        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("── 지시 (vendor/model)", text);
        Assert.Contains("이름 바꿔줘", text);
        Assert.Contains("read_file rect.m → 성공", text);
        Assert.Contains("edit_file rect.m → 실패: ", text);
        Assert.Contains("edit_file rect.m → 실패: Error: old_text was not found in rect.m. The closest part of the file is lines 1-2:", text);
        Assert.Contains("search_files \"rect\" → 성공", text);
        Assert.Contains("Selected code:\n(선택한 코드 2줄 생략)\nThis runs without a window.", text.ReplaceLineEndings("\n"));
        Assert.Contains("── 끝: Done", text);
        Assert.Contains("이름을 바꿨습니다.", text);
        // 파일 내용 · 바꾼 텍스트 · 선택한 코드는 남기지 않는다.
        Assert.DoesNotContain("A = w*h;", text);
        Assert.DoesNotContain("B = 1;", text);
        Assert.DoesNotContain("SECRET_CODE", text);
        Assert.DoesNotContain("SECRET_TWO", text);
    }
}
