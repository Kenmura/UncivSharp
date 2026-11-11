using Godot;
using Unciv.GodotClient.Ui;
using Unciv.Models.Translations;

namespace Unciv.GodotClient.Setup;

/// <summary>
/// T348: the "Kick"/"Ban" confirmation over the lobby screen: the player's name in the title, an optional reason line, and the host's action. The reason is optional
/// and bounded by the host (<see cref="Unciv.Net.Lobby.LobbyLimits.MaxReasonChars"/>); the view only sends what was typed. It never removes a player itself.
/// </summary>
public partial class KickBanView : Modal
{
    private readonly LineEdit reasonField = new() { Name = "ReasonField", PlaceholderText = "Reason (optional)".Tr() };
    private readonly Button confirm = new() { Name = "ReasonConfirm" };
    private readonly Button cancel = new() { Name = "ReasonCancel", Text = "Cancel".Tr() };
    private Action<string?>? apply;

    public KickBanView(UiTheme theme)
    {
        Name = "KickBanPopup";
        Init(theme);
        Body.AddChild(reasonField);
        SetActions([(cancel, ChoiceRole.Ghost), (confirm, ChoiceRole.Danger)]);
        reasonField.TextSubmitted += _ => Confirm();
        confirm.Pressed += Confirm;
        cancel.Pressed += Cancel;
    }

    public LineEdit ReasonField => reasonField;

    public Button ConfirmButton => confirm;

    public Button CancelButton => cancel;

    public string ConfirmText => confirm.Text;

    /// <summary>Shows the popup for one seat: the verb ("Kick"/"Ban") as the header and the confirm label, the reason field empty and focused.</summary>
    public void Open(string verb, string playerName, Action<string?> onApply)
    {
        apply = onApply;
        confirm.Text = verb.Tr();
        SetHeader(verb.Tr() + " " + playerName);
        Fit(400, reasonField);
        reasonField.Text = "";
        Visible = true;
        reasonField.GrabFocus();
    }

    private void Confirm()
    {
        if (apply == null) return;
        string text = reasonField.Text.Trim();
        Visible = false;
        apply(text.Length == 0 ? null : text);
    }

    private void Cancel() => Visible = false;

    protected override bool OnCancel()
    {
        Cancel();
        return true;
    }
}
