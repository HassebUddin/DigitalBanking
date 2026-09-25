import { Component, Input, OnDestroy, signal } from '@angular/core';
import { AppIcon } from './icon';

@Component({
  selector: 'app-voice-note',
  imports: [AppIcon],
  template: `
    <div class="voice-note" [class.mine]="mine">
      <button class="voice-play" type="button" (click)="toggle()" [attr.aria-label]="playing() ? 'Pause' : 'Play'">
        <app-icon [name]="playing() ? 'pause' : 'play'" />
      </button>
      <div class="voice-wave" (click)="toggle()">
        @for (bar of bars; track $index) {
          <span [style.height.px]="bar" [class.on]="$index / bars.length <= progress()"></span>
        }
      </div>
      <strong class="voice-clock">{{ clock() }}</strong>
    </div>
  `
})
export class VoiceNote implements OnDestroy {
  @Input() src = '';
  @Input() duration = 0;
  @Input() mine = false;
  @Input() seed = '';

  playing = signal(false);
  progress = signal(0);

  private audio?: HTMLAudioElement;

  get bars() {
    const key = this.seed || this.src || 'voice';
    return Array.from({ length: 26 }, (_, index) => {
      const wave = Math.abs(Math.sin(key.length * 0.7 + index * 0.62)) * 16;
      return 6 + Math.round(wave);
    });
  }

  toggle() {
    if (!this.src) {
      return;
    }
    if (!this.audio) {
      this.audio = new Audio(this.src);
      this.audio.addEventListener('timeupdate', () => this.syncProgress());
      this.audio.addEventListener('ended', () => {
        this.playing.set(false);
        this.progress.set(0);
      });
    }
    if (this.playing()) {
      this.audio.pause();
      this.playing.set(false);
      return;
    }
    void this.audio.play();
    this.playing.set(true);
  }

  clock() {
    const total = this.duration || Math.round(this.audio?.duration || 0);
    const seconds = this.playing() ? Math.round(this.audio?.currentTime || 0) : total;
    return this.format(seconds || total);
  }

  ngOnDestroy() {
    this.audio?.pause();
    this.audio = undefined;
  }

  private syncProgress() {
    const duration = this.audio?.duration || this.duration || 1;
    this.progress.set(Math.min(1, (this.audio?.currentTime || 0) / duration));
  }

  private format(total: number) {
    const safe = Math.max(0, total);
    const minutes = Math.floor(safe / 60);
    const seconds = safe % 60;
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
  }
}
