#nullable enable
using System;
using AIFramework.Contracts.Localization;
using AIFramework.Features.Localization;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 本地化演示面板：显示当前语言下的文案，点按钮循环切换语言。
    /// <para>
    /// 文案取值一律走 <c>ILocalizationQuery</c>（CQE 查询），UI 不关心表结构；
    /// 缺 key 时按约定回退成 key 本身，面板也把这种情况显式标出来。
    /// </para>
    /// </summary>
    public sealed class LocalizationHud : MonoBehaviour
    {
        [SerializeField] private Text languageText = null!;
        [SerializeField] private Text playText = null!;
        [SerializeField] private Text fallbackText = null!;
        [SerializeField] private Button switchButton = null!;

        private AIFrameworkUnityHost? _host;
        private int _languageIndex;

        private static readonly GameLanguage[] Languages =
        {
            GameLanguage.ChineseSimplified,
            GameLanguage.English,
            GameLanguage.Japanese
        };

        /// <summary>绑定宿主（由 LobbyController 装配完成后调用）。</summary>
        /// <param name="host">已初始化的宿主。</param>
        public void Bind(AIFrameworkUnityHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            if (switchButton != null)
            {
                switchButton.onClick.AddListener(SwitchLanguage);
            }

            Render();
        }

        /// <summary>切到下一门语言并刷新（循环 中 → 英 → 日 → 中）。</summary>
        public void SwitchLanguage()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || !host.LocalizationAvailable)
            {
                return;
            }

            _languageIndex = (_languageIndex + 1) % Languages.Length;
            var command = new SetLanguageCommand(Languages[_languageIndex]);
            host.Bus.Send(in command);

            Render();
        }

        private void OnDestroy()
        {
            if (switchButton != null)
            {
                switchButton.onClick.RemoveListener(SwitchLanguage);
            }
        }

        private void Render()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || !host.LocalizationAvailable)
            {
                if (languageText != null)
                {
                    languageText.text = "本地化：表缺失（TbLocalization.bin）";
                }

                return;
            }

            if (languageText != null)
            {
                languageText.text = "语言：" + Describe(host.Localization.CurrentLanguage);
            }

            if (playText != null)
            {
                playText.text = "ui.play → " + Query(host, "ui.play");
            }

            if (fallbackText != null)
            {
                LocalizedText missing = Query(host, "ui.missing");
                fallbackText.text = "缺 key 回退：" + missing.Text + (missing.Found ? string.Empty : "（未找到，已回退 key）");
            }
        }

        private static LocalizedText Query(AIFrameworkUnityHost host, string key)
        {
            var request = new LocalizationQueryRequest(key);
            return host.Bus.Query<LocalizationQueryRequest, LocalizedText>(in request);
        }

        private static string Describe(GameLanguage language)
        {
            switch (language)
            {
                case GameLanguage.ChineseSimplified:
                    return "简体中文";
                case GameLanguage.English:
                    return "English";
                case GameLanguage.Japanese:
                    return "日本語";
                default:
                    return "未知(" + (int)language + ")";
            }
        }
    }
}
