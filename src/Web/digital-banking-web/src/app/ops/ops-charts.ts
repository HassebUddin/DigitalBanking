import { Component, Directive, ElementRef, OnDestroy, OnInit, computed, input, signal } from '@angular/core';
import { compact, niceMax } from './ops-format';

export interface ChartSeries {
  key: string;
  label: string;
  color: string;
  values: number[];
}

/** Tracks the rendered width of a chart host so SVG geometry is drawn in real pixels. */
@Directive()
abstract class SizedChart implements OnInit, OnDestroy {
  readonly width = signal(600);
  private observer?: ResizeObserver;

  constructor(protected readonly host: ElementRef<HTMLElement>) {}

  ngOnInit() {
    this.observer = new ResizeObserver(([entry]) => this.width.set(Math.max(160, Math.floor(entry.contentRect.width))));
    this.observer.observe(this.host.nativeElement);
  }

  ngOnDestroy() {
    this.observer?.disconnect();
  }
}

const PAD = { top: 10, right: 12, bottom: 22, left: 40 };

/** Stacked area over time with a crosshair tooltip. Used for TPS by province. */
@Component({
  selector: 'ops-stacked-area',
  template: `
    <div class="viz" (mouseleave)="hover.set(null)">
      <svg [attr.width]="width()" [attr.height]="height()" (mousemove)="move($event)" role="img" [attr.aria-label]="ariaLabel()">
        @for (tick of ticks(); track tick.value) {
          <line class="viz-grid" [attr.x1]="pad.left" [attr.x2]="width() - pad.right" [attr.y1]="tick.y" [attr.y2]="tick.y" />
          <text class="viz-tick" [attr.x]="pad.left - 8" [attr.y]="tick.y + 4" text-anchor="end">{{ tick.label }}</text>
        }
        @for (band of bands(); track band.key) {
          <path [attr.d]="band.area" [style.fill]="band.color" fill-opacity="0.22" />
          <path [attr.d]="band.line" [style.stroke]="band.color" fill="none" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />
        }
        <line class="viz-axis" [attr.x1]="pad.left" [attr.x2]="width() - pad.right" [attr.y1]="height() - pad.bottom" [attr.y2]="height() - pad.bottom" />
        @for (label of xLabels(); track label.text) {
          <text class="viz-tick" [attr.x]="label.x" [attr.y]="height() - 6" [attr.text-anchor]="label.anchor">{{ label.text }}</text>
        }
        @if (hover(); as h) {
          <line class="viz-crosshair" [attr.x1]="h.x" [attr.x2]="h.x" [attr.y1]="pad.top" [attr.y2]="height() - pad.bottom" />
        }
      </svg>
      @if (hover(); as h) {
        <div class="viz-tip" [style.left.px]="h.x" [class.flip]="h.x > width() * 0.62">
          <strong>{{ h.title }}</strong>
          @for (row of h.rows; track row.label) {
            <span><i [style.background]="row.color"></i>{{ row.label }}<b>{{ row.value }}</b></span>
          }
          <span class="viz-tip-total">Total<b>{{ h.total }}</b></span>
        </div>
      }
    </div>
  `
})
export class OpsStackedArea extends SizedChart {
  readonly series = input.required<ChartSeries[]>();
  readonly height = input(220);
  readonly unit = input('');
  readonly ariaLabel = input('Stacked area chart');
  readonly pad = PAD;
  readonly hover = signal<{ x: number; title: string; rows: { label: string; color: string; value: string }[]; total: string } | null>(null);

  constructor(host: ElementRef<HTMLElement>) {
    super(host);
  }

  private readonly length = computed(() => Math.max(2, ...this.series().map((series) => series.values.length)));
  private readonly max = computed(() => {
    const totals = Array.from({ length: this.length() }, (_, index) => this.series().reduce((sum, series) => sum + (series.values[index] ?? 0), 0));
    return niceMax(Math.max(...totals, 1));
  });

  private x(index: number) {
    return PAD.left + (index / (this.length() - 1)) * (this.width() - PAD.left - PAD.right);
  }

  private y(value: number) {
    return PAD.top + (1 - value / this.max()) * (this.height() - PAD.top - PAD.bottom);
  }

  readonly ticks = computed(() => [0, 0.25, 0.5, 0.75, 1].map((fraction) => ({
    value: fraction,
    y: this.y(this.max() * fraction),
    label: compact(this.max() * fraction)
  })));

  readonly xLabels = computed(() => {
    const seconds = this.length();
    return [
      { text: `-${seconds}s`, x: PAD.left, anchor: 'start' },
      { text: `-${Math.round(seconds / 2)}s`, x: this.x(Math.floor((seconds - 1) / 2)), anchor: 'middle' },
      { text: 'now', x: this.width() - PAD.right, anchor: 'end' }
    ];
  });

  readonly bands = computed(() => {
    const base = new Array(this.length()).fill(0);
    return this.series().map((series) => {
      const lower = [...base];
      const upper = lower.map((value, index) => value + (series.values[index] ?? 0));
      upper.forEach((value, index) => (base[index] = value));
      const top = upper.map((value, index) => `${this.x(index).toFixed(1)},${this.y(value).toFixed(1)}`);
      const bottom = lower.map((value, index) => `${this.x(index).toFixed(1)},${this.y(value).toFixed(1)}`).reverse();
      return {
        key: series.key,
        color: series.color,
        line: `M${top.join('L')}`,
        area: `M${top.join('L')}L${bottom.join('L')}Z`
      };
    });
  });

  move(event: MouseEvent) {
    const svg = event.currentTarget as SVGElement;
    const offset = event.clientX - svg.getBoundingClientRect().left;
    const ratio = (offset - PAD.left) / (this.width() - PAD.left - PAD.right);
    const index = Math.round(Math.min(1, Math.max(0, ratio)) * (this.length() - 1));
    const rows = [...this.series()].reverse().map((series) => ({
      label: series.label,
      color: series.color,
      value: `${compact(series.values[index] ?? 0)}${this.unit()}`
    }));
    const total = this.series().reduce((sum, series) => sum + (series.values[index] ?? 0), 0);
    this.hover.set({
      x: this.x(index),
      title: index === this.length() - 1 ? 'now' : `${this.length() - 1 - index}s ago`,
      rows,
      total: `${compact(total)}${this.unit()}`
    });
  }
}

/** Stacked columns per time slot (edge traffic, log histogram). */
@Component({
  selector: 'ops-columns',
  template: `
    <div class="viz" (mouseleave)="hover.set(null)">
      <svg [attr.width]="width()" [attr.height]="height()" role="img" [attr.aria-label]="ariaLabel()">
        @for (tick of ticks(); track tick.value) {
          <line class="viz-grid" [attr.x1]="pad.left" [attr.x2]="width() - pad.right" [attr.y1]="tick.y" [attr.y2]="tick.y" />
          <text class="viz-tick" [attr.x]="pad.left - 8" [attr.y]="tick.y + 4" text-anchor="end">{{ tick.label }}</text>
        }
        @for (column of columns(); track column.index) {
          <g (mouseenter)="hover.set(column)">
            <rect class="viz-hit" [attr.x]="column.slotX" [attr.y]="pad.top" [attr.width]="column.slotWidth" [attr.height]="height() - pad.top - pad.bottom" />
            @for (segment of column.segments; track segment.key) {
              <path [attr.d]="segment.d" [style.fill]="segment.color" />
            }
          </g>
        }
        <line class="viz-axis" [attr.x1]="pad.left" [attr.x2]="width() - pad.right" [attr.y1]="height() - pad.bottom" [attr.y2]="height() - pad.bottom" />
        @for (label of xLabels(); track $index) {
          <text class="viz-tick" [attr.x]="label.x" [attr.y]="height() - 6" [attr.text-anchor]="label.anchor">{{ label.text }}</text>
        }
      </svg>
      @if (hover(); as h) {
        <div class="viz-tip" [style.left.px]="h.slotX + h.slotWidth / 2" [class.flip]="h.slotX > width() * 0.62">
          <strong>{{ labels()[h.index] }}</strong>
          @for (row of h.rows; track row.label) {
            <span><i [style.background]="row.color"></i>{{ row.label }}<b>{{ row.value }}</b></span>
          }
        </div>
      }
    </div>
  `
})
export class OpsColumns extends SizedChart {
  readonly series = input.required<ChartSeries[]>();
  readonly labels = input.required<string[]>();
  readonly height = input(160);
  readonly ariaLabel = input('Column chart');
  readonly pad = PAD;
  readonly hover = signal<ReturnType<OpsColumns['columns']>[number] | null>(null);

  constructor(host: ElementRef<HTMLElement>) {
    super(host);
  }

  private readonly count = computed(() => Math.max(1, this.labels().length));
  private readonly max = computed(() => {
    let max = 1;
    for (let index = 0; index < this.count(); index++) {
      max = Math.max(max, this.series().reduce((sum, series) => sum + (series.values[index] ?? 0), 0));
    }
    return niceMax(max);
  });

  private y(value: number) {
    return PAD.top + (1 - value / this.max()) * (this.height() - PAD.top - PAD.bottom);
  }

  readonly ticks = computed(() => [0, 0.5, 1].map((fraction) => ({ value: fraction, y: this.y(this.max() * fraction), label: compact(this.max() * fraction) })));

  readonly xLabels = computed(() => {
    const labels = this.labels();
    if (labels.length === 0) return [];
    const slot = (this.width() - PAD.left - PAD.right) / labels.length;
    return [
      { text: labels[0], x: PAD.left, anchor: 'start' },
      { text: labels[Math.floor(labels.length / 2)], x: PAD.left + slot * Math.floor(labels.length / 2) + slot / 2, anchor: 'middle' },
      { text: labels[labels.length - 1], x: this.width() - PAD.right, anchor: 'end' }
    ];
  });

  readonly columns = computed(() => {
    const slotWidth = (this.width() - PAD.left - PAD.right) / this.count();
    const barWidth = Math.max(1, Math.min(24, slotWidth - 2));
    const baseline = this.height() - PAD.bottom;
    return Array.from({ length: this.count() }, (_, index) => {
      const slotX = PAD.left + index * slotWidth;
      const x = slotX + (slotWidth - barWidth) / 2;
      let cursor = baseline;
      const visible = this.series().filter((series) => (series.values[index] ?? 0) > 0);
      const segments = visible.map((series, position) => {
        const top = cursor - (baseline - this.y(series.values[index] ?? 0));
        // A 2px surface gap separates stacked segments; only the top segment gets rounded corners.
        const bottom = position > 0 ? Math.max(top, cursor - 2) : cursor;
        cursor = top;
        const radius = position === visible.length - 1 ? Math.min(4, barWidth / 2, bottom - top) : 0;
        const d = radius > 0
          ? `M${x},${bottom}V${top + radius}Q${x},${top} ${x + radius},${top}H${x + barWidth - radius}Q${x + barWidth},${top} ${x + barWidth},${top + radius}V${bottom}Z`
          : `M${x},${bottom}V${top}H${x + barWidth}V${bottom}Z`;
        return { key: series.key, color: series.color, d };
      });
      return {
        index,
        slotX,
        slotWidth,
        segments,
        rows: this.series().map((series) => ({ label: series.label, color: series.color, value: compact(series.values[index] ?? 0) }))
      };
    });
  });
}

/** Tiny trend line for tiles and service cards. */
@Component({
  selector: 'ops-sparkline',
  template: `
    <svg [attr.width]="width()" [attr.height]="height()" aria-hidden="true">
      <path [attr.d]="area()" [style.fill]="color()" fill-opacity="0.14" />
      <path [attr.d]="line()" [style.stroke]="color()" fill="none" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />
      @if (last(); as point) {
        <circle [attr.cx]="point.x" [attr.cy]="point.y" r="4" [style.fill]="color()" class="viz-dot" />
      }
    </svg>
  `
})
export class OpsSparkline extends SizedChart {
  readonly values = input.required<number[]>();
  readonly color = input('var(--series-1)');
  readonly height = input(36);
  readonly max = input<number | null>(null);

  constructor(host: ElementRef<HTMLElement>) {
    super(host);
  }

  private readonly points = computed(() => {
    const values = this.values().length > 1 ? this.values() : [0, ...this.values(), 0].slice(-2);
    const max = this.max() ?? Math.max(...values, 1);
    const width = this.width() - 6;
    return values.map((value, index) => ({
      x: 2 + (index / (values.length - 1)) * width,
      y: 5 + (1 - Math.min(value, max) / max) * (this.height() - 10)
    }));
  });

  readonly line = computed(() => 'M' + this.points().map((point) => `${point.x.toFixed(1)},${point.y.toFixed(1)}`).join('L'));
  readonly area = computed(() => {
    const points = this.points();
    return `${this.line()}L${points[points.length - 1].x.toFixed(1)},${this.height()}L${points[0].x.toFixed(1)},${this.height()}Z`;
  });
  readonly last = computed(() => this.points().at(-1));
}

/** Horizontal meter whose fill carries severity. */
@Component({
  selector: 'ops-meter',
  template: `
    <div class="meter" [attr.data-level]="level()">
      <span [style.width.%]="clamped()"></span>
    </div>
  `
})
export class OpsMeter {
  readonly value = input.required<number>();
  readonly warn = input(50);
  readonly critical = input(80);
  readonly clamped = computed(() => Math.max(0, Math.min(100, this.value())));
  readonly level = computed(() => (this.value() >= this.critical() ? 'critical' : this.value() >= this.warn() ? 'warning' : 'good'));
}
