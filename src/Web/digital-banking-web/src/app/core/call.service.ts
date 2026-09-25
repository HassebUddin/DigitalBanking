import { Injectable } from '@angular/core';
import { ChatService } from './chat.service';

@Injectable({ providedIn: 'root' })
export class CallService {
  localStream?: MediaStream;
  remoteStream?: MediaStream;
  private peer?: RTCPeerConnection;
  private currentCallId = '';

  constructor(private readonly chatService: ChatService) {}

  async beginOutgoing(conversationId: string, callType: 'Voice' | 'Video') {
    await this.prepareMedia(callType === 'Video');
    await this.chatService.startCall(conversationId, callType);
    this.watchSignals();
  }

  async acceptIncoming(callId: string, callType: string) {
    this.currentCallId = callId;
    await this.prepareMedia(callType === 'Video');
    this.createPeer();
    await this.chatService.acceptCall(callId);
    this.watchSignals();
    const offer = this.chatService.offerSdp();
    if (offer) {
      await this.handleOffer(offer);
    }
  }

  async hangUp() {
    const call = this.chatService.activeCall() ?? this.chatService.incomingCall();
    if (call) {
      await this.chatService.endCall(call.id);
    }
    this.cleanup();
  }

  private async prepareMedia(includeVideo: boolean) {
    this.localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: includeVideo });
  }

  private createPeer() {
    this.peer = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] });
    this.remoteStream = new MediaStream();
    this.localStream?.getTracks().forEach((track) => this.peer?.addTrack(track, this.localStream!));
    this.peer.ontrack = (event) => event.streams[0].getTracks().forEach((track) => this.remoteStream?.addTrack(track));
    this.peer.onicecandidate = (event) => {
      if (event.candidate && this.currentCallId) {
        void this.chatService.sendIceCandidate(this.currentCallId, JSON.stringify(event.candidate));
      }
    };
  }

  private watchSignals() {
    const offerWatch = setInterval(async () => {
      const offer = this.chatService.offerSdp();
      if (offer && this.peer) {
        this.chatService.offerSdp.set(null);
        await this.handleOffer(offer);
      }
      const answer = this.chatService.answerSdp();
      if (answer && this.peer) {
        this.chatService.answerSdp.set(null);
        await this.peer.setRemoteDescription({ type: 'answer', sdp: answer });
      }
      const candidates = this.chatService.iceCandidates();
      if (candidates.length && this.peer) {
        this.chatService.iceCandidates.set([]);
        for (const candidate of candidates) {
          await this.peer.addIceCandidate(JSON.parse(candidate));
        }
      }
      if (!this.chatService.activeCall() && !this.chatService.incomingCall()) {
        clearInterval(offerWatch);
        this.cleanup();
      }
    }, 400);

    setTimeout(async () => {
      const call = this.chatService.activeCall();
      if (call && call.startedByUserId && !this.peer) {
        this.currentCallId = call.id;
        this.createPeer();
        const offer = await this.peer!.createOffer();
        await this.peer!.setLocalDescription(offer);
        await this.chatService.sendOffer(call.id, offer.sdp ?? '');
      }
    }, 600);
  }

  private async handleOffer(sdp: string) {
    if (!this.peer) {
      this.createPeer();
    }
    await this.peer!.setRemoteDescription({ type: 'offer', sdp });
    const answer = await this.peer!.createAnswer();
    await this.peer!.setLocalDescription(answer);
    const call = this.chatService.activeCall() ?? this.chatService.incomingCall();
    if (call) {
      this.currentCallId = call.id;
      await this.chatService.sendAnswer(call.id, answer.sdp ?? '');
    }
  }

  private cleanup() {
    this.localStream?.getTracks().forEach((track) => track.stop());
    this.peer?.close();
    this.localStream = undefined;
    this.remoteStream = undefined;
    this.peer = undefined;
    this.chatService.incomingCall.set(null);
    this.chatService.activeCall.set(null);
  }
}
