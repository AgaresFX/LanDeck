import { tr } from '../i18n';
// Single "Timer" tab of timer keys: type, duration, gestures, colors and test buttons in one place.
import { socket } from '../net/socket';
import { showToast, useDeck } from '../state/store';
import type { Key, LiveRule, TimerAction } from '../types';
import { ColorField, Section, Segmented, Toggle } from './controls';
import { TIMER_DONE_COLOR, TIMER_RUNNING_COLOR, makeTimerKey, renameTimerInKey, timerName } from './LiveEditor';
import { Icon } from '../ui/Icon';

type Edit = (fn: (k: Key) => void) => void;
type Mode = 'countdown' | 'stopwatch';

const QUICK_MINUTES = [1, 3, 5, 10, 15, 30, 60];

const isRunningRule = (r: LiveRule) => r.when === 'true';
const isDoneRule = (r: LiveRule) => r.when === 'eq' && r.value === '0';

/** Runs a timer command on the PC right away (test buttons, or a reset after a config change). */
function sendTimer(a: TimerAction, op: TimerAction['op'] | 'config') {
  const msg = {
    t: 'edit.timer',
    name: a.name,
    op,
    mode: a.mode ?? 'countdown',
    durationSec: a.durationSec ?? null,
    alertPc: a.alertPc ?? true,
    alertPhone: a.alertPhone ?? true,
  };
  if (!socket.send(msg)) showToast(tr('Non connesso al PC'), 'error');
}

/** Other keys (in any profile) that use the timer with this name. */
function useSharedWith(keyId: string, name: string): number {
  return useDeck((s) => {
    let n = 0;
    for (const p of s.profiles)
      for (const pg of p.pages)
        for (const k of pg.keys) {
          if (k.id === keyId) continue;
          const uses =
            k.live?.source === `timer:${name}` ||
            [k.onPress, k.onLongPress, k.toggle?.onAction, k.toggle?.offAction].some((a) => a?.type === 'timer' && a.name === name);
          if (uses) n++;
        }
    return n;
  });
}

export function TimerSections({ k, edit }: { k: Key; edit: Edit }) {
  const a = k.onPress as TimerAction;
  const mode: Mode = a.mode ?? 'countdown';
  const dur = a.durationSec ?? 300;
  const running = useDeck((s) => !!s.live[`timer:${a.name}`]?.timer?.running);
  const shared = useSharedWith(k.id, a.name);
  const rules = k.live?.rules ?? [];
  const runRule = rules.find(isRunningRule);
  const doneRule = rules.find(isDoneRule);
  const longReset = k.onLongPress?.type === 'timer' && k.onLongPress.op === 'reset' && k.onLongPress.name === a.name;

  /** Changes the timer settings and resets it on the PC, so the key shows the new setting at once. */
  const editTimer = (patch: Partial<TimerAction>) => {
    const next: TimerAction = { ...a, ...patch };
    if (next.mode === 'stopwatch') delete next.durationSec;
    else next.durationSec ??= 300;
    edit((x) => {
      x.onPress = next;
      if (patch.mode && x.live) {
        // The "finished" rule only makes sense for a countdown.
        const others = x.live.rules.filter((r) => !isDoneRule(r));
        x.live.rules =
          patch.mode === 'countdown'
            ? [...others, { when: 'eq', value: '0', style: { bgColor: TIMER_DONE_COLOR, labelText: tr('FINE') } }]
            : others;
      }
    });
    sendTimer(next, 'reset');
  };

  const setDur = (sec: number) => editTimer({ durationSec: Math.max(1, Math.round(sec)) });

  const editRule = (match: (r: LiveRule) => boolean, make: () => LiveRule, on: boolean, fn?: (r: LiveRule) => void) =>
    edit((x) => {
      if (!x.live) return;
      const i = x.live.rules.findIndex(match);
      if (!on) {
        if (i >= 0) x.live.rules.splice(i, 1);
        return;
      }
      if (i < 0) x.live.rules.push(make());
      else fn?.(x.live.rules[i]);
    });

  return (
    <>
      {shared > 0 && (
        <div className="callout warn">
          <div>
            <b>{tr('Timer condiviso con {0} {1}', shared, shared === 1 ? tr('altro tasto') : tr('altri tasti'))}</b>
            <div className="muted small">
              {tr('Premendo uno di questi tasti partono tutti insieme. Rendilo indipendente per usarlo da solo.')}
            </div>
          </div>
          <button
            className="btn primary"
            onClick={() => {
              // Normally the key id; if the other keys already use that name, a fresh variant of it.
              const to = timerName(k) === a.name ? `${k.id}-${Math.random().toString(36).slice(2, 7)}` : timerName(k);
              edit((x) => renameTimerInKey(x, a.name, to));
              sendTimer({ ...a, name: to }, 'reset');
            }}
          >
            {tr('Rendi indipendente')}
          </button>
        </div>
      )}

      <Section title={tr('Tipo')}>
        <Segmented<Mode>
          value={mode}
          options={[
            ['countdown', tr('Conto alla rovescia')],
            ['stopwatch', tr('Cronometro')],
          ]}
          onChange={(m) => m !== mode && editTimer({ mode: m })}
        />
        <p className="muted small hint">
          {mode === 'countdown' ? tr('Parte dalla durata scelta e scende fino a zero.') : tr('Parte da zero e conta il tempo che passa.')}
        </p>
      </Section>

      {mode === 'countdown' && (
        <Section title={tr('Durata')}>
          <div className="timer-duration">
            <label>
              <input
                className="input num"
                type="number"
                inputMode="numeric"
                min={0}
                value={Math.floor(dur / 3600)}
                onChange={(e) => setDur((Number(e.target.value) || 0) * 3600 + (dur % 3600))}
              />
              <span className="muted small">{tr('ore')}</span>
            </label>
            <label>
              <input
                className="input num"
                type="number"
                inputMode="numeric"
                min={0}
                max={59}
                value={Math.floor(dur / 60) % 60}
                onChange={(e) => setDur(Math.floor(dur / 3600) * 3600 + Math.min(59, Number(e.target.value) || 0) * 60 + (dur % 60))}
              />
              <span className="muted small">min</span>
            </label>
            <label>
              <input
                className="input num"
                type="number"
                inputMode="numeric"
                min={0}
                max={59}
                value={dur % 60}
                onChange={(e) => setDur(dur - (dur % 60) + Math.min(59, Number(e.target.value) || 0))}
              />
              <span className="muted small">sec</span>
            </label>
          </div>
          <div className="chips">
            {QUICK_MINUTES.map((m) => (
              <button key={m} type="button" className={`chip${dur === m * 60 ? ' on' : ''}`} onClick={() => setDur(m * 60)}>
                {m < 60 ? `${m} min` : tr('1 ora')}
              </button>
            ))}
          </div>
        </Section>
      )}

      {mode === 'countdown' && (
        <Section title={tr('Avviso a fine timer')}>
          <Toggle
            label={tr('Notifica sul PC')}
            checked={a.alertPc ?? true}
            onChange={(v) => {
              const next = { ...a, alertPc: v };
              edit((x) => (x.onPress = next));
              sendTimer(next, 'config');
            }}
          />
          <Toggle
            label={tr('Suono e vibrazione sul telefono')}
            checked={a.alertPhone ?? true}
            onChange={(v) => {
              const next = { ...a, alertPhone: v };
              edit((x) => (x.onPress = next));
              sendTimer(next, 'config');
            }}
          />
          <p className="muted small hint">
            {tr('Il suono sul telefono funziona solo con PadNook aperto e lo schermo acceso; la notifica sul PC arriva sempre.')}
          </p>
        </Section>
      )}

      <Section title={tr('Comandi')}>
        <p className="muted small">{tr('{0} avvia e mette in pausa.', <b>{tr('Tocco:')}</b>)}</p>
        <Toggle
          label={tr('Tieni premuto per azzerare')}
          checked={longReset}
          onChange={(v) => edit((x) => (x.onLongPress = v ? { type: 'timer', name: a.name, op: 'reset' } : null))}
        />
        <div className="inline-row">
          <button type="button" className="btn" onClick={() => sendTimer(a, 'toggle')}>
            <Icon name={running ? 'pause' : 'play'} />
            {running ? tr('Pausa') : tr('Avvia')}
          </button>
          <button type="button" className="btn" onClick={() => sendTimer(a, 'reset')}>
            {tr('↺ Azzera')}
          </button>
        </div>
        <p className="muted small hint">{tr("Prova il timer da qui: l'anteprima in alto e il tasto sul telefono si aggiornano subito.")}</p>
      </Section>

      <Section title={tr('Colori')}>
        <Toggle
          label={tr('Colore mentre è in corso')}
          checked={!!runRule?.style.bgColor}
          onChange={(v) =>
            editRule(
              isRunningRule,
              () => ({ when: 'true', style: { bgColor: TIMER_RUNNING_COLOR } }),
              v,
              (r) => (r.style.bgColor ??= TIMER_RUNNING_COLOR),
            )
          }
        />
        {runRule?.style.bgColor && (
          <ColorField
            value={runRule.style.bgColor}
            onChange={(c) =>
              editRule(
                isRunningRule,
                () => ({ when: 'true', style: {} }),
                true,
                (r) => (r.style.bgColor = c),
              )
            }
          />
        )}
        {mode === 'countdown' && (
          <>
            <Toggle
              label={tr('Avviso quando finisce')}
              checked={!!doneRule}
              onChange={(v) =>
                editRule(isDoneRule, () => ({ when: 'eq', value: '0', style: { bgColor: TIMER_DONE_COLOR, labelText: tr('FINE') } }), v)
              }
            />
            {doneRule && (
              <>
                <ColorField
                  value={doneRule.style.bgColor ?? TIMER_DONE_COLOR}
                  onChange={(c) =>
                    editRule(
                      isDoneRule,
                      () => ({ when: 'eq', value: '0', style: {} }),
                      true,
                      (r) => (r.style.bgColor = c),
                    )
                  }
                />
                <label className="field">
                  <span>{tr('Testo a fine timer (vuoto = mostra 00:00)')}</span>
                  <input
                    className="input"
                    value={doneRule.style.labelText ?? ''}
                    onChange={(e) =>
                      editRule(
                        isDoneRule,
                        () => ({ when: 'eq', value: '0', style: {} }),
                        true,
                        (r) => (r.style.labelText = e.target.value || undefined),
                      )
                    }
                  />
                </label>
              </>
            )}
          </>
        )}
        <p className="muted small hint">{tr('Testo, icona e colore da fermo si cambiano nella scheda Aspetto.')}</p>
      </Section>

      <Section title={tr('Ricomincia da capo')}>
        <button
          type="button"
          className="btn"
          onClick={() => {
            if (!confirm(tr('Ripristinare il timer con le impostazioni iniziali (anche testo e colori)?'))) return;
            edit((x) => makeTimerKey(x, mode, mode === 'countdown' ? dur : undefined));
            sendTimer({ ...a, name: timerName(k) }, 'reset');
          }}
        >
          {tr('Ripristina impostazioni del timer')}
        </button>
      </Section>
    </>
  );
}
