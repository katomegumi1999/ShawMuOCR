using System;
using System.IO;
using System.Linq;
using ShawMuOCR.Core.Security;
using ShawMuOCR.Core.Settings;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 配置持久化安全性回归测试：
/// 原子写入、损坏配置不再静默吞掉、云厂商凭据全部加密、旧明文与不可解密文不被销毁。
/// </summary>
public class SettingsPersistenceSecurityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SettingsPersistenceSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shawmu_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public void Save_IsAtomic_AndLeavesNoTempFile()
    {
        var service = new JsonSettingsService(_path);
        var settings = service.Current;
        settings.HttpApiPort = 9091;
        service.Save(settings);

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Null(service.LastSaveError);

        // 覆盖写第二次，同样不应该残留临时文件
        settings.HttpApiPort = 9092;
        service.Save(settings);
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Equal(9092, new JsonSettingsService(_path).Current.HttpApiPort);
    }

    [Fact]
    public void Save_EncryptsVolcengineAndAliyunCredentials()
    {
        var service = new JsonSettingsService(_path);
        var settings = service.Current;
        settings.VolcengineAccessKey = "AKLT_volc_access_key";
        settings.VolcengineSecretKey = "volc_secret_value";
        settings.AliyunAccessKeyId = "LTAI_aliyun_access_id";
        settings.AliyunAccessKeySecret = "aliyun_secret_value";
        settings.HttpApiAccessToken = "lan_api_token_value";
        service.Save(settings);

        string json = File.ReadAllText(_path);

        // 落盘文件中不得出现任何一处明文
        Assert.DoesNotContain("AKLT_volc_access_key", json);
        Assert.DoesNotContain("volc_secret_value", json);
        Assert.DoesNotContain("LTAI_aliyun_access_id", json);
        Assert.DoesNotContain("aliyun_secret_value", json);
        Assert.DoesNotContain("lan_api_token_value", json);

        // 内存中仍是明文，供运行期直接使用
        Assert.Equal("AKLT_volc_access_key", service.Current.VolcengineAccessKey);
        Assert.Equal("volc_secret_value", service.Current.VolcengineSecretKey);

        // 重新加载（模拟重启）后应完整还原
        var reloaded = new JsonSettingsService(_path);
        Assert.Equal("AKLT_volc_access_key", reloaded.Current.VolcengineAccessKey);
        Assert.Equal("volc_secret_value", reloaded.Current.VolcengineSecretKey);
        Assert.Equal("LTAI_aliyun_access_id", reloaded.Current.AliyunAccessKeyId);
        Assert.Equal("aliyun_secret_value", reloaded.Current.AliyunAccessKeySecret);
        Assert.Equal("lan_api_token_value", reloaded.Current.HttpApiAccessToken);
    }

    [Fact]
    public void Save_EncryptsBaiduGoogleAndOpenAiKeys()
    {
        var service = new JsonSettingsService(_path);
        var settings = service.Current;
        settings.BaiduAppId = "baidu_app_id_plain";
        settings.BaiduSecretKey = "baidu_secret_plain";
        settings.BaiduApiKey = "baidu_api_key_plain";
        settings.GoogleApiKey = "google_key_plain";
        settings.OpenAiApiKey = "sk-openai-plain";
        service.Save(settings);

        string json = File.ReadAllText(_path);
        Assert.DoesNotContain("baidu_secret_plain", json);
        Assert.DoesNotContain("baidu_api_key_plain", json);
        Assert.DoesNotContain("google_key_plain", json);
        Assert.DoesNotContain("sk-openai-plain", json);
        Assert.DoesNotContain("baidu_app_id_plain", json);

        var reloaded = new JsonSettingsService(_path);
        Assert.Equal("baidu_secret_plain", reloaded.Current.BaiduSecretKey);
        Assert.Equal("baidu_app_id_plain", reloaded.Current.BaiduAppId);
        Assert.Equal("sk-openai-plain", reloaded.Current.OpenAiApiKey);
    }

    [Fact]
    public void Load_LegacyPlaintextCredential_IsKeptAndUpgradedOnNextSave()
    {
        // 模拟旧版本留下的明文配置（历史上火山 / 阿里云密钥就是明文落盘）
        File.WriteAllText(_path, """
        {
          "VolcengineAccessKey": "legacy_plain_volc",
          "AliyunAccessKeySecret": "legacy_plain_aliyun"
        }
        """);

        var service = new JsonSettingsService(_path);

        // 1. 旧明文不得被当成"解密失败"而抹成空
        Assert.Equal("legacy_plain_volc", service.Current.VolcengineAccessKey);
        Assert.Equal("legacy_plain_aliyun", service.Current.AliyunAccessKeySecret);

        // 2. 下一次保存应自动加密升级
        service.Save(service.Current);
        string json = File.ReadAllText(_path);
        Assert.DoesNotContain("legacy_plain_volc", json);
        Assert.Contains("enc:", json);

        // 3. 升级后仍可正确读取
        var reloaded = new JsonSettingsService(_path);
        Assert.Equal("legacy_plain_volc", reloaded.Current.VolcengineAccessKey);
        Assert.Equal("legacy_plain_aliyun", reloaded.Current.AliyunAccessKeySecret);
    }

    [Fact]
    public void Save_DoesNotDestroyCiphertextThatCannotBeDecrypted()
    {
        // 模拟 settings.json 从别的机器/别的 Windows 账户复制过来：
        // 密文存在但当前账户解不开，此时绝不能把密文覆盖成空字符串。
        string foreignCipher = "enc:" + Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.NotEqual(string.Empty, foreignCipher);

        File.WriteAllText(_path, $$"""
        {
          "VolcengineSecretKey": "{{foreignCipher}}"
        }
        """);

        var service = new JsonSettingsService(_path);

        // 内存里只能给出空值（DPAPI 绑定当前用户，解不开）
        Assert.Equal(string.Empty, service.Current.VolcengineSecretKey);

        // 但落盘时必须原样保留原密文
        service.Save(service.Current);
        string json = File.ReadAllText(_path);
        Assert.Contains(foreignCipher, json);
    }

    [Fact]
    public void Load_CorruptJson_BacksUpFileAndRaisesEvent()
    {
        File.WriteAllText(_path, "{ this is definitely not valid json ");

        var service = new JsonSettingsService(_path);

        // 1. 不再静默：留下错误原因
        Assert.False(string.IsNullOrEmpty(service.LastLoadError));

        // 2. 损坏文件已备份，用户仍有找回途径
        string[] backups = Directory.GetFiles(_dir, "settings.json.bad-*");
        Assert.Single(backups);

        // 3. 回落到默认配置而不是崩溃
        Assert.NotNull(service.Current);
        Assert.Equal(8080, service.Current.HttpApiPort);

        // 4. 重复加载会再次触发 LoadFailed 事件
        bool raised = false;
        service.LoadFailed += (_, _) => raised = true;
        service.Load();
        Assert.True(raised);
    }

    [Fact]
    public void AppSettings_Clone_CopiesAccessToken()
    {
        // Clone() 是手写拷贝，新增敏感字段最容易漏（漏了就会导致令牌永远为空）
        var source = new AppSettings
        {
            HttpApiAccessToken = "token-must-survive-clone",
            HttpApiAllowRemote = true
        };

        AppSettings clone = source.Clone();

        Assert.Equal("token-must-survive-clone", clone.HttpApiAccessToken);
        Assert.True(clone.HttpApiAllowRemote);
    }
}
