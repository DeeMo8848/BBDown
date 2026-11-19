using QRCoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using static BBDown.BBDownUtil;
using static BBDown.Core.Logger;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using BBDown.Core.Util;

namespace BBDown
{
    internal class BBDownLoginUtil
    {
        // ★ 2026-11-20 修复：B 站把扫码登录改成了两段式 ——
        //   poll 返回 code=0 时，data.url 里只有 ticket / gourl / first_domain，
        //   没有 SESSDATA；必须再访问 crossDomain（域名是 passport.bilibili.cn，
        //   不是 .com）才在 Set-Cookie 里下发真正的登录态。
        //   旧逻辑直接把 url 的 query 当 cookie 写盘，于是 BBDown.data 里只有
        //   ticket，之后一直被判定为未登录（手机却显示登录成功）。
        private static readonly HttpClient NoRedirectClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // BBDown 读 cookie 时认的字段顺序
        private static readonly string[] CookieOrder =
        {
            "DedeUserID", "DedeUserID__ckMd5", "Expires",
            "SESSDATA", "bili_jct", "gourl", "first_domain"
        };

        /// <summary>
        /// 访问 poll 返回的跳转地址，逐跳读 Set-Cookie 取回登录态。
        /// 必须 AllowAutoRedirect=false 手动跟跳，并且只用 ResponseHeadersRead
        /// —— 最后一跳会落到 bilibili.com 首页，读 body 会白等几十秒。
        /// </summary>
        private static async Task<string> ExchangeCookieAsync(string jumpUrl)
        {
            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string url = jumpUrl;
            for (int hop = 0; hop < 4 && !string.IsNullOrEmpty(url); hop++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
                    req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                    using var resp = await NoRedirectClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                    if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    {
                        foreach (var raw in setCookies)
                        {
                            string first = raw.Split(';')[0];
                            int eq = first.IndexOf('=');
                            if (eq > 0)
                            {
                                string key = first.Substring(0, eq).Trim();
                                if (key.Length > 0)
                                    cookies[key] = first.Substring(eq + 1).Trim();
                            }
                        }
                    }
                    if (cookies.ContainsKey("SESSDATA"))
                        break;
                    url = resp.Headers.Location?.ToString();
                }
                catch (Exception e)
                {
                    LogError($"换取 Cookie 第 {hop + 1} 跳失败: {e.Message}");
                    break;
                }
            }

            var parts = CookieOrder
                .Where(k => cookies.ContainsKey(k) && !string.IsNullOrEmpty(cookies[k]))
                .Select(k => k + "=" + cookies[k]);
            return string.Join(";", parts);
        }

        public static async Task<string> GetLoginStatusAsync(string qrcodeKey)
        {
            string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
            return await HTTPUtil.GetWebSourceAsync(queryUrl);
        }

        public static async Task LoginWEB()
        {
            try
            {
                Log("获取登录地址...");
                string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
                string url = JsonDocument.Parse(await HTTPUtil.GetWebSourceAsync(loginUrl)).RootElement.GetProperty("data").GetProperty("url").ToString();
                string qrcodeKey = GetQueryString("qrcode_key", url);
                //Log(oauthKey);
                //Log(url);
                bool flag = false;
                Log("生成二维码...");
                QRCodeGenerator qrGenerator = new();
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
                PngByteQRCode pngByteCode = new(qrCodeData);
                File.WriteAllBytes("qrcode.png", pngByteCode.GetGraphic(7));
                Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
                var consoleQRCode = new ConsoleQRCode(qrCodeData);
                consoleQRCode.GetGraphic();

                while (true)
                {
                    await Task.Delay(1000);
                    string w = await GetLoginStatusAsync(qrcodeKey);
                    int code = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("code").GetInt32();
                    if (code == 86038)
                    {
                        LogColor("二维码已过期, 请重新执行登录指令.");
                        break;
                    }
                    else if (code == 86101) //等待扫码
                    {
                        continue;
                    }
                    else if (code == 86090) //等待确认
                    {
                        if (!flag)
                        {
                            Log("扫码成功, 请确认...");
                            flag = !flag;
                        }
                    }
                    else
                    {
                        string cc = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("url").ToString();
                        string sessdata = GetQueryString("SESSDATA", cc);
                        string cookieLine;

                        if (string.IsNullOrEmpty(sessdata))
                        {
                            // 两段式登录：poll 只给了 ticket，要再访问 crossDomain 换 cookie
                            Log("登录成功, 正在换取 Cookie...");
                            cookieLine = await ExchangeCookieAsync(cc);
                            if (string.IsNullOrEmpty(cookieLine) || !cookieLine.Contains("SESSDATA="))
                            {
                                LogError("换取 Cookie 失败：没有拿到 SESSDATA，请重试");
                                break;
                            }
                        }
                        else
                        {
                            // 老流程：query 里直接带 SESSDATA
                            cookieLine = cc[(cc.IndexOf('?') + 1)..].Replace("&", ";");
                        }

                        string finalSess = cookieLine.Split(';')
                            .FirstOrDefault(x => x.StartsWith("SESSDATA=")) ?? "";
                        Log("登录成功: " + finalSess);
                        //导出cookie
                        File.WriteAllText(Path.Combine(Program.APP_DIR, "BBDown.data"), cookieLine);
                        File.Delete("qrcode.png");
                        break;
                    }
                }
            }
            catch (Exception e) { LogError(e.Message); }
        }

        public static async Task LoginTV()
        {
            try
            {
                string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
                string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
                var parms = GetTVLoginParms();
                Log("获取登录地址...");
                byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                string web = Encoding.UTF8.GetString(responseArray);
                string url = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("url").ToString();
                string authCode = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("auth_code").ToString();
                Log("生成二维码...");
                QRCodeGenerator qrGenerator = new();
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
                PngByteQRCode pngByteCode = new(qrCodeData);
                File.WriteAllBytes("qrcode.png", pngByteCode.GetGraphic(7));
                Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
                var consoleQRCode = new ConsoleQRCode(qrCodeData);
                consoleQRCode.GetGraphic();
                parms.Set("auth_code", authCode);
                parms.Set("ts", GetTimeStamp(true));
                parms.Remove("sign");
                parms.Add("sign", GetSign(ToQueryString(parms)));
                while (true)
                {
                    await Task.Delay(1000);
                    responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                    web = Encoding.UTF8.GetString(responseArray);
                    string code = JsonDocument.Parse(web).RootElement.GetProperty("code").ToString();
                    if (code == "86038")
                    {
                        LogColor("二维码已过期, 请重新执行登录指令.");
                        break;
                    }
                    else if (code == "86039") //等待扫码
                    {
                        continue;
                    }
                    else
                    {
                        string cc = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("access_token").ToString();
                        Log("登录成功: AccessToken=" + cc);
                        //导出cookie
                        File.WriteAllText(Path.Combine(Program.APP_DIR, "BBDownTV.data"), "access_token=" + cc);
                        File.Delete("qrcode.png");
                        break;
                    }
                }
            }
            catch (Exception e) { LogError(e.Message); }
        }
    }
}
