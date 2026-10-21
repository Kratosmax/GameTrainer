namespace GameTrainer.Core.Updates;

/// <summary>
/// 更新信任根：内置公钥 + 当前通道。
///
/// 公钥必须**编译进程序**，不能从网络获取——否则攻击者同时替换清单与公钥，
/// 签名机制就完全失效。这是整个更新链路唯一的信任锚点。
///
/// 私钥只存在于发布者本机（temp/keys/，已 gitignore），绝不入库。
/// </summary>
internal static class Trust
{
    /// <summary>
    /// 更新清单验签公钥（RSA-3072，SubjectPublicKeyInfo PEM）。
    /// 与私钥 `temp/keys/gametrainer-update-private.pem` 配对，私钥不入库。
    ///
    /// 轮换密钥时必须同时发新版本：老版本内置的是旧公钥，
    /// 用新私钥签的清单在老版本上验签必然失败。
    /// </summary>
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA2nwk57op45RTsaxi98A8
FvJXlXv/0HE/qMrh8vAIVmm/0VKYoKB5pnu9PJWlOGU2Eiy4T1GaoFiFnicErd1Q
tF7ByXAh0oXcMYK9bz+phVFayPDjyjZm5JzZ1+35gaVNjwfCfqGawukBtqDUWvsP
fLL5zPWhsLH0KVhlAy1NdC83C4Nv4ojQFNrVyuVj4ZrrwjSCW372GU1gYgBwCiFm
fi7UrRgLPkbrPrLuBn7FXrFhfg+87sk5NH8bHOfKdunLq94KgnILKhty0MVFh3uS
uH5VaGPLMlvJR1PN8Xrp9EdOrHqqSVScRm2D2RFPwsSJ0xgfneYaMq3gE67rgDiW
IkM8NTfBz0IctqPlF4BuOPUu14mGTw6ol9QbVpSZ+LK/qXDNuzmFliG6c5PqCbI7
JZrUFf8rmpcl+nBEjwZrz2yX8XaVYZhUVDr8ciqzw7/EYBte2jrKn4IHSGw11Lb8
KwzHLpQLoqXBG4uomPOYImZzII93wPZ3p79aJ9OBLjEBAgMBAAE=
-----END PUBLIC KEY-----
""";

    /// <summary>
    /// 当前安装的通道。
    ///
    /// 由打包脚本写入程序目录的 channel.txt，运行时读取；
    /// 文件缺失时按 Lite 处理（体积小、分发更广）。
    /// 通道不匹配的清单会被拒绝——防止 Lite 用户被推 Full 包（或反之）。
    /// </summary>
    public static string CurrentChannel
    {
        get
        {
            try
            {
                string f = Path.Combine(AppContext.BaseDirectory, "channel.txt");
                if (File.Exists(f))
                {
                    string v = File.ReadAllText(f).Trim().ToLowerInvariant();
                    if (v is "lite" or "full") return v;
                }
            }
            catch { }
            return "lite";
        }
    }

    /// <summary>当前程序版本。真源是 Directory.Build.props，编译期注入程序集元数据。</summary>
    public static System.Version CurrentVersion
    {
        get
        {
            var v = typeof(Trust).Assembly.GetName().Version;
            // AssemblyVersion 是 4 段（1.0.0.0），清单要求 3 段
            return v == null
                ? new System.Version(0, 0, 0)
                : new System.Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
}
