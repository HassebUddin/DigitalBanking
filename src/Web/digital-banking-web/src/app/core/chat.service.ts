import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import * as signalR from '@microsoft/signalr';
import { AuthService } from './auth.service';
import { ChatCall, ChatConversation, ChatMessage, DirectoryUser } from './chat.models';

@Injectable({ providedIn: 'root' })
export class ChatService {
  readonly conversations = signal<ChatConversation[]>([]);
  readonly messages = signal<ChatMessage[]>([]);
  readonly directory = signal<DirectoryUser[]>([]);
  readonly onlineUserIds = signal<string[]>([]);
  readonly typingName = signal('');
  readonly incomingCall = signal<ChatCall | null>(null);
  readonly activeCall = signal<ChatCall | null>(null);
  readonly offerSdp = signal<string | null>(null);
  readonly answerSdp = signal<string | null>(null);
  readonly iceCandidates = signal<string[]>([]);

  private connection?: signalR.HubConnection;
  private currentConversationId = '';

  constructor(
    private readonly http: HttpClient,
    private readonly authService: AuthService
  ) {}

  get apiUrl() {
    return this.authService.apiUrl;
  }

  fileUrl(path?: string | null) {
    return path ? `${this.apiUrl}${path}` : '';
  }

  async connect() {
    if (this.connection) {
      return;
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${this.apiUrl}/hubs/chat`, {
        accessTokenFactory: () => this.authService.accessToken ?? ''
      })
      .withAutomaticReconnect()
      .build();

    this.connection.on('MessageReceived', (message: ChatMessage) => {
      if (message.conversationId === this.currentConversationId) {
        this.messages.update((items) => [...items, message]);
      }
      this.loadConversations();
    });
    this.connection.on('UserTyping', (payload: { conversationId: string; displayName: string }) => {
      if (payload.conversationId === this.currentConversationId) {
        this.typingName.set(payload.displayName);
        setTimeout(() => this.typingName.set(''), 2000);
      }
    });
    this.connection.on('OnlineUsers', (userIds: string[]) => this.onlineUserIds.set(userIds));
    this.connection.on('UserPresenceChanged', (payload: { userId: string; isOnline: boolean }) => {
      this.onlineUserIds.update((ids) =>
        payload.isOnline ? Array.from(new Set([...ids, payload.userId])) : ids.filter((id) => id !== payload.userId)
      );
    });
    this.connection.on('IncomingCall', (call: ChatCall) => {
      if (call.startedByUserId === this.authService.userId()) {
        this.activeCall.set(call);
        return;
      }
      this.incomingCall.set(call);
    });
    this.connection.on('CallAccepted', (call: ChatCall) => this.activeCall.set(call));
    this.connection.on('CallEnded', () => {
      this.incomingCall.set(null);
      this.activeCall.set(null);
    });
    this.connection.on('CallOffer', (payload: { sdp: string }) => this.offerSdp.set(payload.sdp));
    this.connection.on('CallAnswer', (payload: { sdp: string }) => this.answerSdp.set(payload.sdp));
    this.connection.on('CallIceCandidate', (payload: { candidate: string }) => {
      this.iceCandidates.update((items) => [...items, payload.candidate]);
    });

    await this.connection.start();
  }

  loadDirectory() {
    this.http.get<DirectoryUser[]>(`${this.apiUrl}/api/chat/directory`).subscribe((users) => this.directory.set(users));
  }

  loadConversations() {
    this.http.get<ChatConversation[]>(`${this.apiUrl}/api/chat/conversations`).subscribe((conversations) => this.conversations.set(conversations));
  }

  async openConversation(conversation: ChatConversation) {
    this.currentConversationId = conversation.id;
    await this.connection?.invoke('JoinConversation', conversation.id);
    this.http.get<ChatMessage[]>(`${this.apiUrl}/api/chat/conversations/${conversation.id}/messages`).subscribe((messages) => this.messages.set(messages));
  }

  startDirect(otherUserId: string) {
    return this.http.post<ChatConversation>(`${this.apiUrl}/api/chat/conversations/direct`, { otherUserId });
  }

  startGroup(title: string, memberUserIds: string[]) {
    return this.http.post<ChatConversation>(`${this.apiUrl}/api/chat/conversations/group`, { title, memberUserIds });
  }

  startSupport() {
    return this.http.post<ChatConversation>(`${this.apiUrl}/api/chat/conversations/support`, {});
  }

  async sendText(conversationId: string, body: string) {
    await this.connection?.invoke('SendText', conversationId, body);
  }

  async notifyTyping(conversationId: string) {
    await this.connection?.invoke('NotifyTyping', conversationId);
  }

  uploadFile(conversationId: string, file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<ChatMessage>(`${this.apiUrl}/api/chat/conversations/${conversationId}/files`, form);
  }

  uploadVoice(conversationId: string, file: Blob, durationSeconds: number) {
    const form = new FormData();
    form.append('file', file, 'voice-note.webm');
    form.append('durationSeconds', String(durationSeconds));
    return this.http.post<ChatMessage>(`${this.apiUrl}/api/chat/conversations/${conversationId}/voice`, form);
  }

  async startCall(conversationId: string, callType: 'Voice' | 'Video') {
    await this.connection?.invoke('StartCall', conversationId, callType);
  }

  async acceptCall(callId: string) {
    await this.connection?.invoke('AcceptCall', callId);
  }

  async declineCall(callId: string) {
    await this.connection?.invoke('DeclineCall', callId);
  }

  async endCall(callId: string) {
    await this.connection?.invoke('EndCall', callId);
  }

  async sendOffer(callId: string, sdp: string) {
    await this.connection?.invoke('SendOffer', callId, sdp);
  }

  async sendAnswer(callId: string, sdp: string) {
    await this.connection?.invoke('SendAnswer', callId, sdp);
  }

  async sendIceCandidate(callId: string, candidate: string) {
    await this.connection?.invoke('SendIceCandidate', callId, candidate);
  }

  createEmployee(payload: { email: string; password: string; fullName: string; role: string }) {
    return this.http.post<DirectoryUser>(`${this.apiUrl}/api/auth/employees`, payload);
  }
}
