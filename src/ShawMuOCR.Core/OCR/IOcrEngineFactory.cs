using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.OCR;

public interface IOcrEngineFactory
{
    IOcrEngine Create(string modelName);
    IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen) => Create(modelName);
    IOcrEngine Create(AppSettings settings) => Create(settings.Model, settings.EnableAngleDetection, settings.MaxSideLen);
}
