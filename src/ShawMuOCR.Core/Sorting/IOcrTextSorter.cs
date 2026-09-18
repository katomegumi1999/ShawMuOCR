using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Sorting;

public interface IOcrTextSorter
{
    string SortAndFormat(IReadOnlyList<OcrTextBlock> blocks);
}
