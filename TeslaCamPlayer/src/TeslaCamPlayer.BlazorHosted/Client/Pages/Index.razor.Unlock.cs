using System;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index
{
    private bool _isDecrypting;

    private async Task<Clip> PrepareEncryptedClipAsync(Clip clip)
    {
        if (string.IsNullOrWhiteSpace(clip.DirectoryPath))
            return null;

        var status = await GetTeslaStatusAsync();
        if (status is not { Connected: true })
        {
            var openSettings = await DialogService.ShowMessageBox(
                "Encrypted clips",
                status is { HasToken: true }
                    ? "Your Tesla session has expired. Reconnect your account in Settings to decrypt these clips."
                    : "These clips are encrypted. Connect your Tesla account in Settings to unlock them.",
                yesText: "Open Settings", cancelText: "Not now");

            if (openSettings == true)
                await OpenSettingsDialog();

            return null;
        }

        _isDecrypting = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var response = await HttpClient.PostAsync(
                $"Api/PrepareEvent?path={Uri.EscapeDataString(clip.DirectoryPath)}", null);
            var result = await response.ReadFromNewtonsoftJsonAsync<PrepareEventResponse>();

            if (result is { Success: true, Clip: not null })
            {
                ReplaceLoadedClip(result.Clip);
                return result.Clip;
            }

            var message = result?.ErrorCode switch
            {
                "NotConnected" => "Connect your Tesla account in Settings to decrypt these clips.",
                "RefreshFailed" => "Your Tesla session has expired. Reconnect your account in Settings.",
                _ => "Could not decrypt this event. Its clips may only be readable inside the vehicle."
            };
            await DialogService.ShowMessageBox("Decryption failed", message);
            return null;
        }
        catch
        {
            await DialogService.ShowMessageBox("Decryption failed", "Something went wrong while decrypting this event.");
            return null;
        }
        finally
        {
            _isDecrypting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<TeslaConnectionStatus> GetTeslaStatusAsync()
    {
        try
        {
            return await HttpClient.GetFromNewtonsoftJsonAsync<TeslaConnectionStatus>("Api/TeslaStatus");
        }
        catch
        {
            return null;
        }
    }

    // Swap the freshly decrypted clip into the loaded list so its lock badge clears without a full reload.
    private void ReplaceLoadedClip(Clip decrypted)
    {
        if (decrypted?.DirectoryPath == null)
            return;

        foreach (var key in _loadedClips.Keys.ToList())
        {
            if (string.Equals(_loadedClips[key].DirectoryPath, decrypted.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                _loadedClips[key] = decrypted;
        }
    }
}
