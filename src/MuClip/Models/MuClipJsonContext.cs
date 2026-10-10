namespace MuClip.Models;

// 剪贴板核心所有跨语言数据交换均已切换为 ReadOnlySpan<byte> + BinaryPrimitives 二进制帧零拷贝解析，无需 JSON 序列化上下文。
