import { Component, OnDestroy, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { readErrorMessage } from '../../core/http-error';
import { AppIcon } from '../../ui/icon';
import { RuleType } from '../ops.models';
import { OpsService, ruleLabel } from '../ops.service';

const TYPES: { type: RuleType; label: string; hint: string; needsLimit: boolean; placeholder: string }[] = [
  { type: 'block-client', label: 'Block client', hint: 'Return 403 for one client id or IP.', needsLimit: false, placeholder: 'bot-185.220.101.47' },
  { type: 'rate-limit-client', label: 'Rate-limit client', hint: 'Allow N requests per second, 429 above that.', needsLimit: true, placeholder: 'app-karachi-042' },
  { type: 'block-region', label: 'Suspend region', hint: 'Reject traffic and transactions from a province.', needsLimit: false, placeholder: 'Balochistan' },
  { type: 'block-path', label: 'Disable endpoint', hint: 'Reject every request whose path starts with this prefix.', needsLimit: false, placeholder: '/api/transactions/transfer' },
  { type: 'throttle-service', label: 'Throttle service', hint: 'Cap total requests per second routed to one service.', needsLimit: true, placeholder: 'account' }
];

@Component({
  selector: 'app-rules-page',
  imports: [FormsModule, AppIcon],
  template: `
    <div class="ops-page">
      <header class="ops-head">
        <div>
          <p class="ops-eyebrow">Gateway protection</p>
          <h1>Traffic rules</h1>
        </div>
        <p class="ops-head-note">Rules are pushed to the API gateway every 2 seconds and enforced on every request. Blocked calls still appear in the logs with their reason.</p>
      </header>

      <section class="ops-grid two">
        <article class="panel">
          <div class="panel-head"><h2>New rule</h2></div>
          <div class="rule-types">
            @for (option of types; track option.type) {
              <button type="button" class="scenario" [class.on]="form.type === option.type" (click)="form.type = option.type">
                <strong>{{ option.label }}</strong>
                <small>{{ option.hint }}</small>
              </button>
            }
          </div>
          <div class="form-grid">
            <label class="span-2">Value
              @if (form.type === 'block-region') {
                <select [(ngModel)]="form.value">
                  @for (region of regions; track region) { <option [value]="region">{{ region }}</option> }
                </select>
              } @else {
                <input [(ngModel)]="form.value" [placeholder]="current().placeholder" />
              }
            </label>
            @if (current().needsLimit) {
              <label>Requests per second
                <input type="number" min="1" [(ngModel)]="form.limit" />
              </label>
            }
            <label>Expires after
              <select [(ngModel)]="form.expires">
                <option [ngValue]="null">Never</option>
                <option [ngValue]="5">5 minutes</option>
                <option [ngValue]="30">30 minutes</option>
                <option [ngValue]="240">4 hours</option>
              </select>
            </label>
            <label class="span-2">Reason (shown in logs)
              <input [(ngModel)]="form.reason" placeholder="Why this rule exists" />
            </label>
          </div>
          <div class="btn-row">
            <button type="button" class="ops-btn primary" [disabled]="busy()" (click)="create()"><app-icon name="shield" />Activate rule</button>
          </div>
        </article>

        <article class="panel">
          <div class="panel-head">
            <div>
              <h2>Active rules</h2>
              <p>{{ ops.overview()?.rules?.length ?? 0 }} active · sign-in and this console are never blocked</p>
            </div>
            @if ((ops.overview()?.rules?.length ?? 0) > 0) {
              <button type="button" class="ops-btn small danger" (click)="removeAll()">Remove all</button>
            }
          </div>
          <ul class="rules">
            @for (rule of ops.overview()?.rules ?? []; track rule.id) {
              <li>
                <div>
                  <span class="tag" [class.critical]="rule.type.startsWith('block')" [class.warning]="!rule.type.startsWith('block')">{{ label(rule.type) }}</span>
                  <strong class="mono">{{ rule.value }}</strong>
                  @if (rule.limitPerSecond) { <span class="muted"> · {{ rule.limitPerSecond }} req/s</span> }
                  <p>{{ rule.reason || 'No reason given' }} · by {{ rule.createdBy }} · {{ since(rule.createdAtUtc) }}
                    @if (rule.expiresAtUtc) { · expires {{ until(rule.expiresAtUtc) }} }
                  </p>
                </div>
                <button type="button" class="ops-btn small" (click)="ops.removeRule(rule.id)">Remove</button>
              </li>
            } @empty {
              <li class="muted">No rules. Traffic flows freely.</li>
            }
          </ul>
        </article>
      </section>
    </div>
  `
})
export class RulesPage implements OnDestroy {
  readonly ops = inject(OpsService);
  private readonly release = this.ops.watch();
  readonly types = TYPES;
  readonly regions = ['Punjab', 'Sindh', 'Khyber Pakhtunkhwa', 'Balochistan'];
  readonly busy = signal(false);
  readonly label = ruleLabel;
  form: { type: RuleType; value: string; limit: number; expires: number | null; reason: string } = {
    type: 'block-client',
    value: '',
    limit: 10,
    expires: null,
    reason: ''
  };

  current() {
    return TYPES.find((option) => option.type === this.form.type)!;
  }

  create() {
    const value = this.form.type === 'block-region' ? this.form.value || this.regions[0] : this.form.value.trim();
    if (!value) {
      this.ops.notify('error', 'Enter what the rule applies to.');
      return;
    }

    this.busy.set(true);
    this.ops.addRule({
      type: this.form.type,
      value,
      limitPerSecond: this.current().needsLimit ? this.form.limit : null,
      reason: this.form.reason,
      expiresInMinutes: this.form.expires
    }).subscribe({
      next: () => {
        this.busy.set(false);
        this.ops.notify('ok', `${ruleLabel(this.form.type)} active for ${value}`);
        this.form.value = '';
        this.form.reason = '';
        this.ops.refresh();
      },
      error: (error) => {
        this.busy.set(false);
        this.ops.notify('error', readErrorMessage(error, 'Could not create the rule.'));
      }
    });
  }

  removeAll() {
    this.ops.reset({ rules: true, chaos: false, pods: false, databases: false, stopSimulation: false });
  }

  since(utc: string) {
    const minutes = Math.round((Date.now() - new Date(utc).getTime()) / 60000);
    return minutes < 1 ? 'just now' : minutes < 60 ? `${minutes}m ago` : `${Math.round(minutes / 60)}h ago`;
  }

  until(utc: string) {
    const minutes = Math.max(0, Math.round((new Date(utc).getTime() - Date.now()) / 60000));
    return minutes < 60 ? `in ${minutes}m` : `in ${Math.round(minutes / 60)}h`;
  }

  ngOnDestroy() {
    this.release();
  }
}
