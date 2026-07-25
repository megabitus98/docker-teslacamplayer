using System.Collections.Generic;
using TeslaCamPlayer.BlazorHosted.Server.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface ISeiParserService
{
    List<SeiMetadata> ExtractSeiMessages(string videoFilePath);
    SeiMetadata GetSeiForTime(List<SeiMetadata> messages, double timeSeconds, double frameRate = 30.0);
}
