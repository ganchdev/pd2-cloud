namespace PD2Cloud.Desktop.Services;

public interface IFolderPicker
{
    Task<string?> PickAsync();
}
