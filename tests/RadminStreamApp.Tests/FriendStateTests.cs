using System.ComponentModel;
using RadminStreamApp.Models;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// A máquina de estados que decide se o card do amigo é clicável e onde ele fica na lista.
/// Sem cobertura até aqui, apesar de ser a lógica que já causou o bug do card "conectando"
/// preso na tela ao clicar em alguém que não estava em live.
/// </summary>
public class FriendStateTests
{
    private static Friend Make(bool online = false, bool streaming = false, bool watching = false)
        => new() { Name = "Amigo", Ip = "26.10.0.5", IsOnline = online, IsStreaming = streaming, IsWatching = watching };

    [Theory]
    [InlineData(false, false, false, false)] // offline
    [InlineData(true, false, false, false)]  // online, mas sem transmitir: nada a assistir
    [InlineData(true, true, false, true)]    // em live: dá para entrar
    [InlineData(true, true, true, true)]     // assistindo: o clique sai da live
    [InlineData(false, false, true, true)]   // caiu enquanto você assistia: ainda dá para sair
    public void CanWatchOnlyWhenThereIsSomethingToJoinOrLeave(
        bool online, bool streaming, bool watching, bool expected)
    {
        Assert.Equal(expected, Make(online, streaming, watching).CanWatch);
    }

    [Fact]
    public void SortRankPutsTheUsefulCardsFirst()
    {
        var watching = Make(online: true, streaming: true, watching: true);
        var live = Make(online: true, streaming: true);
        var online = Make(online: true);
        var offline = Make();

        Assert.True(watching.SortRank < live.SortRank);
        Assert.True(live.SortRank < online.SortRank);
        Assert.True(online.SortRank < offline.SortRank);
    }

    [Fact]
    public void DisplayNameFallsBackToTheIpWhenThereIsNoNickname()
    {
        Assert.Equal("26.10.0.5", new Friend { Ip = "26.10.0.5", Name = "" }.DisplayName);
        Assert.Equal("26.10.0.5", new Friend { Ip = "26.10.0.5", Name = "   " }.DisplayName);
        Assert.Equal("Fulano", new Friend { Ip = "26.10.0.5", Name = "Fulano" }.DisplayName);
    }

    [Fact]
    public void SubtitleShowsSessionInfoOnlyWhileConnected()
    {
        var friend = Make(online: true, streaming: true);

        Assert.Equal("26.10.0.5", friend.SubtitleText);

        friend.SessionInfo = "42ms";
        Assert.Equal("26.10.0.5 · 42ms", friend.SubtitleText);

        friend.SessionInfo = null;
        Assert.Equal("26.10.0.5", friend.SubtitleText);
    }

    [Fact]
    public void StatusColorDistinguishesTheThreeStates()
    {
        Assert.Equal("#00D26A", Make(online: true, streaming: true).StatusColor);
        Assert.Equal("#FFBB00", Make(online: true).StatusColor);
        Assert.Equal("#4A4A52", Make().StatusColor);
    }

    /// <summary>
    /// Os derivados só chegam à tela via PropertyChanged. Se IsStreaming não avisasse que
    /// CanWatch mudou, o card continuaria inerte depois de o amigo entrar em live.
    /// </summary>
    [Fact]
    public void ChangingStreamingNotifiesTheDerivedProperties()
    {
        var friend = Make(online: true);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)friend).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        friend.IsStreaming = true;

        Assert.Contains(nameof(Friend.CanWatch), changed);
        Assert.Contains(nameof(Friend.SortRank), changed);
        Assert.Contains(nameof(Friend.StatusColor), changed);
        Assert.Contains(nameof(Friend.StatusTooltip), changed);
    }

    [Fact]
    public void ChangingIpNotifiesDisplayName()
    {
        var friend = new Friend { Ip = "26.10.0.5" };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)friend).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        friend.Ip = "26.10.0.6";

        Assert.Contains(nameof(Friend.DisplayName), changed);
    }

    [Fact]
    public void SessionInfoNotifiesSubtitle()
    {
        var friend = Make(online: true, streaming: true);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)friend).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        friend.SessionInfo = "18ms";

        Assert.Contains(nameof(Friend.SubtitleText), changed);
    }
}
