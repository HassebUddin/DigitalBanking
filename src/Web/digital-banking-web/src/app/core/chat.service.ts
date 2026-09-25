import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import * as signalR from '@microsoft/signalr';
import { AuthService } from './auth.service';
import { ChatCall, ChatConversation, ChatMessage, DirectoryUser, IncomingToast, MessageReceipt } from './chat.models';
import { holdSkeleton } from '../ui/hold-skeleton';

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
  readonly inboxLoading = signal(false);
  readonly messagesLoading = signal(false);
  readonly directoryLoading = signal(false);
  readonly incomingToast = signal<IncomingToast | null>(null);
  readonly pendingOpen = signal<ChatConversation | null>(null);

  private connection?: signalR.HubConnection;
  private currentConversationId = '';
  private inboxStartedAt = 0;
  private messagesStartedAt = 0;
  private directoryStartedAt = 0;
  private toastTimer = 0;

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
        this.upsertMessage(message);
        if (message.senderUserId.toLowerCase() !== this.authService.userId().toLowerCase()) {
          void this.acknowledgeRead(message.conversationId);
        }
      }
      if (message.senderUserId.toLowerCase() !== this.authService.userId().toLowerCase()) {
        this.showIncomingToast(message);
      }
      this.loadConversations();
    });
    this.connection.on('MessageReceipts', (receipts: MessageReceipt[]) => this.applyReceipts(receipts));
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

  loadDirectory(forceHold = false) {
    const holding = forceHold || this.directoryLoading() || this.directory().length === 0;
    if (forceHold || this.directory().length === 0) {
      this.directoryStartedAt = Date.now();
      this.directoryLoading.set(true);
      if (forceHold) {
        this.directory.set([]);
      }
    }
    this.http.get<DirectoryUser[]>(`${this.apiUrl}/api/chat/directory`).subscribe({
      next: (users) => {
        if (holding && this.directoryLoading()) {
          holdSkeleton(this.directoryStartedAt, () => {
            this.directory.set(users);
            this.directoryLoading.set(false);
          });
          return;
        }
        this.directory.set(users);
      },
      error: () => {
        if (holding && this.directoryLoading()) {
          holdSkeleton(this.directoryStartedAt, () => {
            this.directory.set([]);
            this.directoryLoading.set(false);
          });
          return;
        }
        this.directory.set([]);
      }
    });
  }

  loadConversations(forceHold = false) {
    const holding = forceHold || this.inboxLoading() || this.conversations().length === 0;
    if (forceHold || this.conversations().length === 0) {
      this.inboxStartedAt = Date.now();
      this.inboxLoading.set(true);
      if (forceHold) {
        this.conversations.set([]);
      }
    }
    this.http.get<ChatConversation[]>(`${this.apiUrl}/api/chat/conversations`).subscribe({
      next: (conversations) => {
        if (holding && this.inboxLoading()) {
          holdSkeleton(this.inboxStartedAt, () => {
            this.conversations.set(conversations);
            this.inboxLoading.set(false);
          });
          return;
        }
        this.conversations.set(conversations);
      },
      error: () => {
        if (holding && this.inboxLoading()) {
          holdSkeleton(this.inboxStartedAt, () => {
            this.conversations.set([]);
            this.inboxLoading.set(false);
          });
          return;
        }
        this.conversations.set([]);
      }
    });
  }

  isOnline(userId?: string | null) {
    if (!userId) {
      return false;
    }
    const id = userId.toLowerCase();
    return this.onlineUserIds().some((item) => item.toLowerCase() === id);
  }

  applyReceipts(receipts: MessageReceipt[]) {
    if (!receipts?.length) {
      return;
    }

    this.messages.update((items) =>
      items.map((message) => {
        const receipt = receipts.find((item) => item.messageId === message.id);
        return receipt ? { ...message, receiptStatus: receipt.receiptStatus } : message;
      })
    );
    this.conversations.update((items) =>
      items.map((conversation) => {
        const receipt = receipts.find((item) => item.conversationId === conversation.id && item.messageId === conversation.lastMessageId);
        return receipt ? { ...conversation, lastMessageReceiptStatus: receipt.receiptStatus } : conversation;
      })
    );
  }

  async openConversation(conversation: ChatConversation) {
    this.currentConversationId = conversation.id;
    this.messages.set([]);
    this.messagesStartedAt = Date.now();
    this.messagesLoading.set(true);
    await this.connection?.invoke('JoinConversation', conversation.id);
    this.http.get<ChatMessage[]>(`${this.apiUrl}/api/chat/conversations/${conversation.id}/messages`).subscribe({
      next: (messages) => {
        holdSkeleton(this.messagesStartedAt, () => {
          this.messages.set(messages);
          this.messagesLoading.set(false);
        });
      },
      error: () => {
        holdSkeleton(this.messagesStartedAt, () => {
          this.messages.set([]);
          this.messagesLoading.set(false);
        });
      }
    });
  }

  closeConversation() {
    this.currentConversationId = '';
    this.messages.set([]);
    this.messagesLoading.set(false);
    this.typingName.set('');
  }

  dismissToast() {
    window.clearTimeout(this.toastTimer);
    this.incomingToast.set(null);
  }

  private showIncomingToast(message: ChatMessage) {
    const conversation =
      this.conversations().find((item) => item.id === message.conversationId) ?? {
        id: message.conversationId,
        title: message.senderName || 'New message',
        conversationType: 'Direct',
        createdAtUtc: message.createdAtUtc,
        lastMessage: this.toastPreview(message),
        unreadCount: 1,
        members: []
      };
    this.incomingToast.set({
      conversationId: message.conversationId,
      senderName: message.senderName || conversation.title,
      preview: this.toastPreview(message),
      conversation
    });
    window.clearTimeout(this.toastTimer);
    this.toastTimer = window.setTimeout(() => this.incomingToast.set(null), 6500);
    this.playChime();
  }

  private toastPreview(message: ChatMessage) {
    if (message.messageType === 'Voice') {
      return 'Voice message';
    }
    if (message.messageType === 'File') {
      return message.fileName || 'Sent a file';
    }
    const text = (message.body || '').trim();
    return text.length > 72 ? `${text.slice(0, 72)}...` : text || 'New message';
  }

  private playChime() {
    const AudioContextCtor = window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    if (!AudioContextCtor) {
      return;
    }
    const context = new AudioContextCtor();
    const oscillator = context.createOscillator();
    const gain = context.createGain();
    oscillator.type = 'sine';
    oscillator.frequency.setValueAtTime(880, context.currentTime);
    oscillator.frequency.exponentialRampToValueAtTime(1240, context.currentTime + 0.14);
    gain.gain.setValueAtTime(0.0001, context.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.07, context.currentTime + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, context.currentTime + 0.32);
    oscillator.connect(gain);
    gain.connect(context.destination);
    oscillator.start();
    oscillator.stop(context.currentTime + 0.34);
  }

  async acknowledgeRead(conversationId: string) {
    await this.connection?.invoke('AcknowledgeRead', conversationId);
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
    const text = body.trim();
    if (!text) {
      return;
    }
    this.upsertMessage({
      id: `pending-${Date.now()}`,
      conversationId,
      senderUserId: this.authService.userId(),
      senderName: this.authService.fullName(),
      messageType: 'Text',
      body: text,
      receiptStatus: 'Sent',
      createdAtUtc: new Date().toISOString()
    });
    await this.connection?.invoke('SendText', conversationId, text);
  }

  upsertMessage(message: ChatMessage) {
    this.messages.update((items) => {
      const pendingIndex = items.findIndex(
        (item) =>
          item.id.startsWith('pending-') &&
          item.body === message.body &&
          item.senderUserId.toLowerCase() === message.senderUserId.toLowerCase()
      );
      if (pendingIndex >= 0 && !message.id.startsWith('pending-')) {
        const next = [...items];
        next[pendingIndex] = message;
        return next;
      }
      if (items.some((item) => item.id === message.id)) {
        return items;
      }
      return [...items, message];
    });
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
