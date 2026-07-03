using System.Text.Json;

namespace TikTokPublisher.Ui.Services;

internal static class EmbeddedBrowserScripts
{
  public const string LocalStorageExport = """
(() => JSON.stringify({
  origin: window.location.origin,
  localStorage: Array.from({ length: window.localStorage ? window.localStorage.length : 0 }, (_, index) => {
    const name = window.localStorage.key(index);
    return { name, value: window.localStorage.getItem(name) || '' };
  }).filter((entry) => entry.name)
}))();
""";

  public static string BuildLoginAutofillScript(string email, string password)
  {
    var emailJson = JsonSerializer.Serialize(email ?? "");
    var passwordJson = JsonSerializer.Serialize(password ?? "");
    return LoginAutofillTemplate
      .Replace("__EMAIL__", emailJson, StringComparison.Ordinal)
      .Replace("__PASSWORD__", passwordJson, StringComparison.Ordinal);
  }

  /// <summary>
  /// 对齐 tiktokdramacenter.com/login 真实 DOM（Semi Design）：
  /// #email、#password、.semi-select（登录方式）、button.semi-button-primary（登录）。
  /// </summary>
  private const string LoginAutofillTemplate = """
(() => {
  const email = __EMAIL__;
  const password = __PASSWORD__;

  const isVisible = (element) => {
    if (!element) return false;
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
  };

  const allText = (element) => [
    element.innerText,
    element.textContent,
    element.getAttribute && element.getAttribute('aria-label'),
    element.getAttribute('aria-labelledby'),
    element.getAttribute('title')
  ].filter(Boolean).join(' ').trim();

  const setValue = (input, value) => {
    if (!input || value == null || value === '') return false;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
    if (setter) setter.call(input, value);
    else input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    input.dispatchEvent(new Event('blur', { bubbles: true }));
    return true;
  };

  const clickElement = (element) => {
    if (!element || !isVisible(element)) return false;
    element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
    element.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
    element.click();
    return true;
  };

  const findEmailInput = () =>
    document.querySelector('#email')
    || document.querySelector('input[placeholder*="mail" i], input[placeholder*="邮箱" i]');

  const findPasswordInput = () => {
    const byId = document.querySelector('#password');
    if (byId && isVisible(byId)) return byId;
    return Array.from(document.querySelectorAll('input[type="password"]')).find(isVisible) || null;
  };

  const findLoginModeSelect = () =>
    Array.from(document.querySelectorAll('.semi-select')).find((element) => {
      if (!isVisible(element)) return false;
      if ((element.className || '').includes('Select__trigger')) return false;
      const text = allText(element);
      return /验证码|verification|password|密码|code/i.test(text);
    }) || null;

  const isPasswordMode = () => {
    const pwd = findPasswordInput();
    if (pwd) return true;
    const mode = findLoginModeSelect();
    const text = mode ? allText(mode) : '';
    return /password|密码/i.test(text) && !/verification|验证码|code/i.test(text);
  };

  const switchToPasswordMode = (done) => {
    if (!password || isPasswordMode()) {
      done();
      return;
    }
    const modeSelect = findLoginModeSelect();
    if (!modeSelect) {
      done();
      return;
    }
    const trigger = modeSelect.querySelector('.semi-select-selection') || modeSelect;
    clickElement(trigger);
    window.setTimeout(() => {
      const options = Array.from(document.querySelectorAll('.semi-select-option,[role="option"]'))
        .filter((element) => isVisible(element) && /password|密码/i.test(allText(element))
          && !/verification|验证码|code|send|发送/i.test(allText(element)));
      const option = options.find((element) => /^(password|密码)$/i.test(allText(element).trim())) || options[0];
      clickElement(option);
      window.setTimeout(done, 400);
    }, 280);
  };

  const acceptAgreement = () => {
    const checkbox = document.querySelector('input.semi-checkbox-input[type="checkbox"]')
      || Array.from(document.querySelectorAll('input[type="checkbox"]')).find(isVisible);
    if (checkbox && !checkbox.checked) {
      clickElement(checkbox.closest('.semi-checkbox') || checkbox);
      return;
    }
    const roleCheckbox = Array.from(document.querySelectorAll('[role="checkbox"]'))
      .find((element) => isVisible(element) && element.getAttribute('aria-checked') !== 'true');
    clickElement(roleCheckbox);
  };

  const clickLoginButton = () => {
    const primary = Array.from(document.querySelectorAll('button.semi-button-primary'))
      .find((element) => isVisible(element) && /log\s*in|sign\s*up|登录|注册/i.test(allText(element)));
    if (clickElement(primary)) return true;
    const fallback = Array.from(document.querySelectorAll('button,[role="button"]'))
      .find((element) => isVisible(element)
        && /log\s*in|sign\s*up|登录|注册/i.test(allText(element))
        && !/send|发送|tiktok|continue|继续/i.test(allText(element)));
    return clickElement(fallback);
  };

  const runPipeline = (shouldSubmit) => {
    const emailInput = findEmailInput();
    setValue(emailInput, email);
    switchToPasswordMode(() => {
      const passwordInput = findPasswordInput();
      setValue(passwordInput, password);
      acceptAgreement();
      if (shouldSubmit && password) clickLoginButton();
    });
  };

  runPipeline(false);
  window.setTimeout(() => runPipeline(false), 700);
  window.setTimeout(() => runPipeline(true), 1400);
  window.setTimeout(() => runPipeline(true), 2600);
})();
""";
}
