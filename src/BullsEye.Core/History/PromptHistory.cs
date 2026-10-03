namespace BullsEye.Core;

/// <summary>최근 지시문 목록 (최신순). 팝업에서 ↑로 불러온다.</summary>
public sealed class PromptHistory(int capacity)
{
    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items => _items;

    public void Add(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _items.Remove(prompt);
        _items.Insert(0, prompt);
        if (_items.Count > capacity)
        {
            _items.RemoveRange(capacity, _items.Count - capacity);
        }
    }
}
