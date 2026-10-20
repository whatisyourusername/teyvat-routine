using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TeyvatRoutine;

// A task owns UI selection; its original segments still own all recordings and images.
public sealed class RoutineTask : INotifyPropertyChanged, IDisposable
{
    private readonly Profile _profile;
    private string? _activeStatus;
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<Segment> Parts { get; }
    public bool IsGroup => Id == "battle";
    public IEnumerable<int> RoundChoices => Enumerable.Range(1, 10);
    public int Rounds
    {
        get => _profile.DomainRuns;
        set { if (value is < 1 or > 10 || value == _profile.DomainRuns) return; _profile.DomainRuns = value; Changed(); Changed(nameof(Status)); Changed(nameof(Ready)); }
    }
    public bool IsSelected
    {
        get => _profile.IsStepSelected(Id);
        set
        {
            var selection = _profile.RoutineSegments.Where(s => _profile.IsStepSelected(s.Id)).Select(s => s.Id).ToHashSet();
            foreach (var part in Parts) { if (value) selection.Add(part.Id); else selection.Remove(part.Id); }
            _profile.SelectedSteps = _profile.RoutineSegments.Where(s => selection.Contains(s.Id)).Select(s => s.Id).ToList();
            Changed();
        }
    }
    public bool Ready => Parts.All(s => s.Id == "login" && _profile.ScreenFlow.LoginEnabled
        ? s.Before != null && s.After != null && s.Width > 0 && _profile.ScreenFlow.Validate(s.Width, s.Height, true).Count == 0
        : s.Ready || (s.Id == "retry" && Rounds == 1));
    public string Status => _activeStatus ?? (IsGroup ? $"전투·보상 {Rounds}회 · {(Ready ? "등록 완료" : "등록 필요")}"
        : Id == "login" && _profile.ScreenFlow.LoginEnabled ? "문·필드 확인으로 자동 진입" : Parts[0].Status);
    public RoutineTask(Profile profile, string id, string name, params string[] members)
    {
        _profile = profile; Id = id; Name = name;
        Parts = members.Select(profile.Get).ToArray();
        foreach (var s in Parts) s.PropertyChanged += PartChanged;
    }
    private void PartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Segment.Status) && sender is Segment s)
            _activeStatus = s.Status == s.SetupLabel ? null : s.EditorLabel + " · " + s.Status;
        Changed(nameof(Status)); Changed(nameof(Ready));
    }
    public void Dispose() { foreach (var s in Parts) s.PropertyChanged -= PartChanged; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
