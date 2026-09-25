import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';

@Component({
  selector: 'app-signature-pad',
  template: `
    <div class="sign-pad">
      <p>Sign inside the box with your finger or mouse</p>
      <canvas #canvas (pointerdown)="beginStroke($event)" (pointermove)="drawStroke($event)" (pointerup)="endStroke($event)" (pointerleave)="endStroke($event)"></canvas>
      <button class="secondary" type="button" (click)="clear()">Clear signature</button>
    </div>
  `
})
export class SignaturePad implements AfterViewInit, OnDestroy {
  @ViewChild('canvas', { static: true }) private readonly canvasRef!: ElementRef<HTMLCanvasElement>;

  private drawing = false;
  private hasInk = false;
  private resizeObserver?: ResizeObserver;

  ngAfterViewInit() {
    this.fitCanvas();
    this.resizeObserver = new ResizeObserver(() => this.fitCanvas());
    this.resizeObserver.observe(this.canvasRef.nativeElement.parentElement ?? this.canvasRef.nativeElement);
  }

  ngOnDestroy() {
    this.resizeObserver?.disconnect();
  }

  get isEmpty() {
    return !this.hasInk;
  }

  clear() {
    const canvas = this.canvasRef.nativeElement;
    const context = canvas.getContext('2d');
    if (!context) {
      return;
    }
    context.setTransform(1, 0, 0, 1, 0, 0);
    context.clearRect(0, 0, canvas.width, canvas.height);
    this.prepareSurface();
    this.hasInk = false;
  }

  toFile(fileName = 'signature.png') {
    if (this.isEmpty) {
      return null;
    }

    const dataUrl = this.canvasRef.nativeElement.toDataURL('image/png');
    const bytes = atob(dataUrl.split(',')[1]);
    const buffer = new Uint8Array(bytes.length);
    for (let index = 0; index < bytes.length; index += 1) {
      buffer[index] = bytes.charCodeAt(index);
    }
    return new File([buffer], fileName, { type: 'image/png' });
  }

  toDataUrl() {
    return this.isEmpty ? '' : this.canvasRef.nativeElement.toDataURL('image/png');
  }

  beginStroke(event: PointerEvent) {
    const context = this.canvasContext();
    if (!context) {
      return;
    }
    const point = this.pointFromEvent(event);
    this.drawing = true;
    this.hasInk = true;
    context.beginPath();
    context.moveTo(point.x, point.y);
    this.canvasRef.nativeElement.setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  drawStroke(event: PointerEvent) {
    if (!this.drawing) {
      return;
    }
    const context = this.canvasContext();
    if (!context) {
      return;
    }
    const point = this.pointFromEvent(event);
    context.lineTo(point.x, point.y);
    context.stroke();
    event.preventDefault();
  }

  endStroke(event: PointerEvent) {
    if (!this.drawing) {
      return;
    }
    this.drawing = false;
    this.canvasContext()?.closePath();
    if (this.canvasRef.nativeElement.hasPointerCapture(event.pointerId)) {
      this.canvasRef.nativeElement.releasePointerCapture(event.pointerId);
    }
  }

  private fitCanvas() {
    const canvas = this.canvasRef.nativeElement;
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.clientWidth || 320;
    const height = canvas.clientHeight || 160;
    const snapshot = this.hasInk ? canvas.toDataURL('image/png') : '';
    canvas.width = width * ratio;
    canvas.height = height * ratio;
    this.prepareSurface();
    if (snapshot) {
      const image = new Image();
      image.onload = () => this.canvasContext()?.drawImage(image, 0, 0, canvas.width, canvas.height);
      image.src = snapshot;
    }
  }

  private prepareSurface() {
    const canvas = this.canvasRef.nativeElement;
    const context = canvas.getContext('2d');
    if (!context) {
      return;
    }
    const ratio = window.devicePixelRatio || 1;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.lineWidth = 2.2;
    context.lineCap = 'round';
    context.lineJoin = 'round';
    context.strokeStyle = '#12221f';
  }

  private canvasContext() {
    return this.canvasRef.nativeElement.getContext('2d');
  }

  private pointFromEvent(event: PointerEvent) {
    const bounds = this.canvasRef.nativeElement.getBoundingClientRect();
    return {
      x: event.clientX - bounds.left,
      y: event.clientY - bounds.top
    };
  }
}
