namespace BullsEye.Core;

/// <summary>
/// 요청마다 쓸 수 있는 access token을 준다. 만료가 가까우면 갱신하고, 바뀐 계정(교체된 refresh token 포함)을
/// <c>persist</c>로 저장한다. 갱신은 한 번에 하나만 (refresh token이 매번 바뀌어 동시에 하면 서로 무효가 된다).
/// </summary>
public sealed class ChatGptTokenSource(
    ChatGptAuth auth,
    ChatGptAccount account,
    Func<ChatGptAccount, Task> persist,
    TimeProvider time)
{
    /// <summary>만료까지 이만큼 남으면 미리 갱신한다 (긴 스트리밍 중 만료 방지).</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public ChatGptAccount Account { get; private set; } = account;

    /// <exception cref="ChatGptException">로그인 안 됨, 또는 갱신 토큰이 만료되어 다시 로그인해야 함.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(Account)) return Account.AccessToken!;
        if (!Account.IsSignedIn) throw ChatGptException.NotSignedIn();

        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            // 기다리는 동안 다른 요청이 이미 갱신했을 수 있다.
            if (IsFresh(Account)) return Account.AccessToken!;

            ChatGptAccount refreshed;
            try
            {
                refreshed = await auth.RefreshAsync(Account, cancellationToken);
            }
            catch (ChatGptException)
            {
                // 다시 로그인해야 하는 상태: 쓸 수 없는 토큰을 남기지 않는다.
                Account = Account.SignedOut();
                await PersistAsync();
                throw;
            }

            Account = refreshed;
            await PersistAsync();
            return refreshed.AccessToken!;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private bool IsFresh(ChatGptAccount account) =>
        account.IsSignedIn && account.AccessTokenExpiresAt - time.GetUtcNow() > RefreshMargin;

    /// <summary>
    /// 저장 실패(USB 일시 오류 등)로 요청까지 실패시키지는 않는다.
    /// 메모리에는 새 토큰이 있으니 이번 실행 동안은 계속 쓸 수 있다.
    /// </summary>
    private async Task PersistAsync()
    {
        try
        {
            await persist(Account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
