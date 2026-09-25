import { Component, Input } from '@angular/core';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-doc-preview',
  template: `
    <div class="doc-viewer">
      @if (isImage) {
        <img [attr.src]="href" [alt]="title" />
      } @else {
        <iframe [attr.src]="href" [title]="title" class="doc-frame"></iframe>
      }
      <a class="doc-open" [href]="href" target="_blank" rel="noreferrer">Open full size</a>
    </div>
  `
})
export class DocPreview {
  @Input({ required: true }) path = '';
  @Input() title = 'Document';

  constructor(private readonly authService: AuthService) {}

  get href() {
    if (!this.path) {
      return '';
    }
    return this.path.startsWith('http') ? this.path : `${this.authService.apiUrl}${this.path}`;
  }

  get isImage() {
    return !/\.pdf($|\?)/i.test(this.path);
  }
}
