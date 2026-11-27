import { getLangSetting, setLangSetting, type LangSetting } from '../i18n';

/** Language picker (per device). Labels are bilingual so anyone can find it. Changing it reloads the page. */
export function LanguageSelect() {
  return (
    <label className="field">
      <span>Lingua · Language</span>
      <select className="input" value={getLangSetting()} onChange={(e) => setLangSetting(e.target.value as LangSetting)}>
        <option value="auto">Automatica · Automatic</option>
        <option value="it">Italiano</option>
        <option value="en">English</option>
      </select>
    </label>
  );
}
