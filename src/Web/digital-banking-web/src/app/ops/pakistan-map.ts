import { Component, computed, input, output, signal } from '@angular/core';
import { ProvinceLive } from './ops.models';
import { compact, pkr } from './ops-format';
import { PK_MAP, projectLatLng } from './pakistan-map.data';

// Sequential blue ramp from the theme tokens: quiet regions recede into the surface, busy ones stand out.
const RAMP = Array.from({ length: 10 }, (_, step) => `var(--ramp-${step})`);
const IDLE = 'var(--map-idle)';

const LABELS: Record<string, [number, number]> = {
  Punjab: [30.9, 72.3],
  Sindh: [26.3, 68.5],
  Balochistan: [28.4, 65.6],
  'Khyber Pakhtunkhwa': [33.4, 70.9]
};

// Regions drawn for geography but not served by a cluster; Islamabad belongs to the Punjab cluster.
const OUTSIDE = new Set(['Gilgit-Baltistan', 'Azad Kashmir']);
const SERVED_BY: Record<string, string> = { Islamabad: 'Punjab' };

@Component({
  selector: 'ops-pakistan-map',
  template: `
    <div class="pk-map" (mouseleave)="hovered.set(null)">
      <svg [attr.viewBox]="viewBox" role="img" aria-label="Live transactions per province across Pakistan">
        <defs>
          <pattern id="pk-suspended" patternUnits="userSpaceOnUse" width="14" height="14" patternTransform="rotate(45)">
            <rect width="14" height="14" style="fill: var(--suspend-bg)" />
            <line x1="0" y1="0" x2="0" y2="14" style="stroke: var(--critical)" stroke-width="5" stroke-opacity="0.55" />
          </pattern>
        </defs>
        @for (shape of shapes(); track shape.name) {
          @if (shape.outside) {
            <path class="pk-outside" [attr.d]="shape.path" />
          } @else {
            <path
              class="pk-province"
              [class.selected]="selected() === shape.region"
              [class.hovered]="hovered()?.name === shape.region"
              [class.db-down]="shape.dbDown"
              [attr.d]="shape.path"
              [style.fill]="shape.fill"
              (mouseenter)="hover(shape.region, $event)"
              (mousemove)="hover(shape.region, $event)"
              (click)="pick.emit(shape.region)"
            />
          }
        }
        @for (label of labels(); track label.name) {
          <text class="pk-label" [attr.x]="label.x" [attr.y]="label.y" text-anchor="middle">{{ label.name }}</text>
        }
        @for (city of cities(); track city.name) {
          @if (city.tps > 0) {
            <circle class="pk-pulse" [attr.cx]="city.x" [attr.cy]="city.y" [attr.r]="city.r" [style.animation-duration.s]="city.pulse" />
          }
          <circle class="pk-city" [class.idle]="city.tps === 0" [attr.cx]="city.x" [attr.cy]="city.y" [attr.r]="city.r">
            <title>{{ city.name }} · {{ city.tps }} TPS · {{ city.customersLabel }} customers</title>
          </circle>
          @if (city.major) {
            <text class="pk-city-label" [attr.x]="city.x + city.r + 6" [attr.y]="city.y + 5">{{ city.name }}</text>
          }
        }
      </svg>

      @if (hovered(); as h) {
        <div class="viz-tip map-tip" [style.left.%]="h.left" [style.top.%]="h.top">
          <strong>{{ h.province.name }}</strong>
          @if (h.province.suspended) {
            <span class="tip-flag">Suspended by admin</span>
          }
          @if (h.province.dbState !== 'healthy') {
            <span class="tip-flag">Database {{ h.province.dbState }}</span>
          }
          <span>Primary DB<b>{{ h.province.primaryNode ?? 'none' }}</b></span>
          <span>Live TPS<b>{{ h.province.tps }}</b></span>
          <span>Customers<b>{{ compact(h.province.customers) }}</b></span>
          <span>Volume / min<b>{{ pkr(h.province.volumePerMinute) }}</b></span>
          <span>Failure rate<b>{{ h.province.failureRate }}%</b></span>
          @if (h.province.spikeLabel) {
            <span>{{ h.province.spikeLabel }}<b>{{ h.province.multiplier }}x</b></span>
          }
        </div>
      }

      <div class="pk-legend">
        <span>TPS</span>
        <i class="pk-ramp"></i>
        <span>0 – {{ compact(maxTps()) }}</span>
        <span class="pk-legend-key"><i class="pk-suspended-key"></i>Suspended</span>
        <span class="pk-legend-key"><i class="pk-outside-key"></i>No regional cluster</span>
      </div>
    </div>
  `
})
export class OpsPakistanMap {
  readonly provinces = input.required<ProvinceLive[]>();
  readonly selected = input<string | null>(null);
  readonly pick = output<string>();
  readonly hovered = signal<{ name: string; province: ProvinceLive; left: number; top: number } | null>(null);
  readonly viewBox = `0 0 ${PK_MAP.width} ${PK_MAP.height}`;
  readonly compact = compact;
  readonly pkr = pkr;

  readonly maxTps = computed(() => Math.max(1, ...this.provinces().map((province) => province.tps)));

  readonly shapes = computed(() => {
    const byName = new Map(this.provinces().map((province) => [province.name, province]));
    return PK_MAP.provinces.map((shape) => {
      const region = SERVED_BY[shape.name] ?? shape.name;
      const live = byName.get(region);
      let fill = IDLE;
      if (live?.suspended) {
        fill = 'url(#pk-suspended)';
      } else if (live && live.tps > 0) {
        // Square-root scale keeps small provinces visible next to Punjab and Sindh.
        const level = Math.sqrt(live.tps / this.maxTps());
        fill = RAMP[Math.min(RAMP.length - 1, Math.round(level * (RAMP.length - 1)))];
      }
      return { name: shape.name, region, path: shape.path, fill, outside: OUTSIDE.has(shape.name), dbDown: live?.dbState === 'down' };
    });
  });

  readonly labels = computed(() =>
    Object.entries(LABELS).map(([name, [lat, lng]]) => {
      const [x, y] = projectLatLng(lat, lng);
      return { name: name === 'Khyber Pakhtunkhwa' ? 'KP' : name, x, y };
    })
  );

  readonly cities = computed(() => {
    const all = this.provinces().flatMap((province) => province.cities);
    const maxCity = Math.max(1, ...all.map((city) => city.tps));
    const majors = new Set(['Karachi', 'Lahore', 'Islamabad', 'Peshawar', 'Quetta', 'Multan', 'Gwadar', 'Hyderabad']);
    return all.map((city) => {
      const [x, y] = projectLatLng(city.lat, city.lng);
      return {
        name: city.name,
        x,
        y,
        tps: city.tps,
        r: city.tps > 0 ? 5 + Math.sqrt(city.tps / maxCity) * 11 : 4,
        pulse: Math.max(0.8, 3 - Math.sqrt(city.tps / maxCity) * 2.2),
        major: majors.has(city.name),
        customersLabel: compact(city.customers)
      };
    });
  });

  hover(name: string, event: MouseEvent) {
    const province = this.provinces().find((item) => item.name === name);
    const host = (event.currentTarget as SVGElement).ownerSVGElement?.getBoundingClientRect();
    if (!province || !host) {
      return;
    }

    this.hovered.set({
      name,
      province,
      left: ((event.clientX - host.left) / host.width) * 100,
      top: ((event.clientY - host.top) / host.height) * 100
    });
  }
}
