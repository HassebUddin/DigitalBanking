import { Injectable, WritableSignal, effect, signal, untracked } from '@angular/core';

export interface Toast {
  id: number;
  kind: 'error' | 'success';
  text: string;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);
  private nextId = 1;

  error(text: string) {
    this.show('error', text);
  }

  success(text: string) {
    this.show('success', text);
  }

  // Call from a component field/constructor: every time a page sets its error or success text, it pops as a toast.
  // The text is cleared after it pops, so the same message set twice still shows twice.
  watch(errors: Array<() => WritableSignal<string>>, successes: Array<() => WritableSignal<string>> = []) {
    errors.forEach((get) => effect(() => this.drain('error', get())));
    successes.forEach((get) => effect(() => this.drain('success', get())));
    return true;
  }

  private drain(kind: Toast['kind'], source: WritableSignal<string>) {
    const text = source();
    if (text) {
      this.show(kind, text);
      untracked(() => source.set(''));
    }
  }

  dismiss(id: number) {
    this.toasts.update((list) => list.filter((toast) => toast.id !== id));
  }

  private show(kind: Toast['kind'], text: string) {
    const id = this.nextId++;
    // Same message twice in a row (double tap) shows once.
    this.toasts.update((list) => [...list.filter((toast) => toast.text !== text), { id, kind, text }].slice(-3));
    setTimeout(() => this.dismiss(id), 4000);
  }
}
