using CommunityToolkit.Mvvm.Input;

namespace E2ee.App.ViewModels;

/// <summary>"Use another account": the accounts on this computer, and a way to make a new one.</summary>
public sealed partial class AccountsViewModel : ViewModelBase
{
    private readonly Action<AppOptions.LocalAccount> _pick;
    private readonly Action _createNew, _back;

    public AccountsViewModel(IReadOnlyList<AppOptions.LocalAccount> accounts, string currentDirectory,
        Action<AppOptions.LocalAccount> pick, Action createNew, Action back)
    {
        (_pick, _createNew, _back) = (pick, createNew, back);
        Accounts = accounts.Select(a => new AccountItem(a, a.Options.DataDirectory == currentDirectory, this)).ToList();
    }

    public IReadOnlyList<AccountItem> Accounts { get; }
    public bool HasAccounts => Accounts.Count > 0;

    [RelayCommand] private void CreateNew() => _createNew();
    [RelayCommand] private void Back() => _back();

    internal void Pick(AppOptions.LocalAccount a) => _pick(a);
}

public sealed partial class AccountItem(AppOptions.LocalAccount account, bool isCurrent, AccountsViewModel owner)
{
    public string Title => account.Title;
    public string Initial => Title[..1].ToUpperInvariant();
    public string Detail => (account.Locked ? "Password protected" : "No password") + (isCurrent ? " · last used" : "");

    [RelayCommand] private void Open() => owner.Pick(account);
}
