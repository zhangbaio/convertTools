using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IDramaSettingsProvider
{
    DramaSourceSettings Get();

    void SavePikachuDeviceId(string deviceId);
}
