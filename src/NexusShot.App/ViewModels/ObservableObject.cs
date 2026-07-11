using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace NexusShot.App.ViewModels;
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
