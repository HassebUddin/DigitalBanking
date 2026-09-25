import { Component, ElementRef, OnInit, ViewChild, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../core/auth.service';
import { CallService } from '../core/call.service';
import { ChatService } from '../core/chat.service';
import { ChatConversation } from '../core/chat.models';
import { readErrorMessage } from '../core/http-error';
import { AppModal } from '../ui/modal';
import { AppIcon } from '../ui/icon';
import { ListSkeleton } from '../ui/list-skeleton';
import { VoiceNote } from '../ui/voice-note';

@Component({
  selector: 'app-chat-page',
  imports: [FormsModule, AppModal, AppIcon, ListSkeleton, VoiceNote],
  template: `
    <section class="chat-page" [class.thread-open]="!!selectedConversation()">
      @if (!selectedConversation()) {
        <div class="inbox">
          <header class="chat-head">
            <div>
              <p>Digital Bank</p>
              <h1>Chats</h1>
            </div>
            @if (authService.isStaff) {
              <button class="round-btn" type="button" (click)="openComposer()" aria-label="New chat">
                <app-icon name="plus" />
              </button>
            }
          </header>
          <div class="chat-search">
            <input name="chatSearch" [(ngModel)]="searchText" placeholder="Search chats" />
          </div>
          @if (error()) { <p class="error chat-error">{{ error() }}</p> }
          <div class="inbox-list">
            @if (!authService.isStaff) {
              <button class="wa-row" type="button" (click)="openSupport()">
                <span class="wa-avatar-wrap">
                  <span class="wa-avatar support">S</span>
                  @if (isSupportOnline()) {
                    <span class="online-dot"></span>
                  }
                </span>
                <div class="wa-copy">
                  <div class="wa-top">
                    <strong>Bank Support</strong>
                    <small>Now</small>
                  </div>
                  <p>Need help with your account?</p>
                </div>
                @if (isSupportOnline()) {
                  <span class="active-badge">Active</span>
                }
              </button>
            }
            @if (chatService.inboxLoading()) {
              <app-list-skeleton />
            } @else {
            @for (conversation of visibleConversations(); track conversation.id) {
              <button class="wa-row" type="button" (click)="selectConversation(conversation)">
                <span class="wa-avatar-wrap">
                  <span class="wa-avatar">{{ initial(conversation.title) }}</span>
                  @if (isConversationOnline(conversation)) {
                    <span class="online-dot"></span>
                  }
                </span>
                <div class="wa-copy">
                  <div class="wa-top">
                    <strong>{{ conversation.title }}</strong>
                    <small>{{ shortTime(conversation.lastMessageAtUtc || conversation.createdAtUtc) }}</small>
                  </div>
                  <p class="wa-preview">
                    @if (isMineLast(conversation)) {
                      <span class="inbox-ticks" [class.read]="conversation.lastMessageReceiptStatus === 'Read'">
                        <app-icon [name]="conversation.lastMessageReceiptStatus === 'Sent' ? 'tick' : 'ticks'" />
                      </span>
                    }
                    {{ previewLast(conversation.lastMessage, conversation.conversationType) }}
                  </p>
                </div>
                @if (isConversationOnline(conversation)) {
                  <span class="active-badge">Active</span>
                }
                @if (conversation.unreadCount > 0) {
                  <span class="unread-pill">{{ conversation.unreadCount }}</span>
                }
              </button>
            }
            }
          </div>
        </div>
      } @else {
        <div class="thread">
          <header class="thread-head">
            <button class="round-btn" type="button" (click)="closeThread()" aria-label="Back">
              <app-icon name="back" />
            </button>
            <span class="wa-avatar-wrap">
              <span class="wa-avatar sm">{{ initial(selectedConversation()?.title || 'C') }}</span>
              @if (selectedConversation(); as conversation) {
                @if (isConversationOnline(conversation)) {
                  <span class="online-dot"></span>
                }
              }
            </span>
            <div class="thread-who">
              <strong>
                {{ selectedConversation()?.title }}
                @if (selectedConversation(); as conversation) {
                  @if (isConversationOnline(conversation)) {
                    <span class="active-badge">Active</span>
                  }
                }
              </strong>
              @if (chatService.typingName()) {
                <small>typing...</small>
              } @else if (selectedConversation(); as conversation) {
                <small [class.online-label]="isConversationOnline(conversation)">{{ threadPresence(conversation) }}</small>
              }
            </div>
            <button class="round-btn" type="button" (click)="startCall('Video')" aria-label="Video call">
              <app-icon name="video" />
            </button>
            <button class="round-btn" type="button" (click)="startCall('Voice')" aria-label="Voice call">
              <app-icon name="phone" />
            </button>
          </header>
          <div class="message-list" #messagePane>
            @if (chatService.messagesLoading()) {
              <app-list-skeleton [count]="5" />
            } @else {
              <div class="message-stack">
                @for (message of chatService.messages(); track message.id) {
                  <article class="bubble" [class.mine]="message.senderUserId === authService.userId()">
                    @if (message.messageType === 'Text' || message.messageType === 'Call' || message.messageType === 'System') {
                      <p>{{ message.body }}</p>
                    }
                    @if (message.messageType === 'File') {
                      <a [href]="chatService.fileUrl(message.fileUrl)" target="_blank">{{ message.fileName }}</a>
                    }
                    @if (message.messageType === 'Voice') {
                      <app-voice-note
                        [src]="chatService.fileUrl(message.fileUrl)"
                        [duration]="message.durationSeconds || 0"
                        [mine]="message.senderUserId === authService.userId()"
                        [seed]="message.id"
                      />
                    }
                    <small class="bubble-meta">
                      {{ shortTime(message.createdAtUtc) }}
                      @if (message.senderUserId === authService.userId()) {
                        <span class="receipt" [class.read]="message.receiptStatus === 'Read'">
                          <app-icon [name]="message.receiptStatus === 'Sent' ? 'tick' : 'ticks'" />
                        </span>
                      }
                    </small>
                  </article>
                }
              </div>
            }
          </div>
          <footer class="composer" [class.is-recording]="recording()">
            @if (recording()) {
              <button class="composer-icon rec-cancel" type="button" (click)="cancelVoice()" aria-label="Cancel voice">
                <app-icon name="trash" />
              </button>
              <div class="rec-live">
                <span class="rec-dot"></span>
                <strong>{{ recClock() }}</strong>
                <div class="rec-wave">
                  @for (bar of recBars; track bar) {
                    <span></span>
                  }
                </div>
              </div>
              <button class="send-btn" type="button" (click)="toggleVoice()" aria-label="Send voice">
                <app-icon name="send-msg" />
              </button>
            } @else {
              <input #fileInput type="file" hidden (change)="onFileSelected($event)" />
              <button class="composer-icon" type="button" (click)="fileInput.click()" aria-label="Attach file">
                <app-icon name="attach" />
              </button>
              <input name="draft" [(ngModel)]="draft" (ngModelChange)="onTyping()" (keydown.enter)="sendText()" placeholder="Type a message" />
              <button class="composer-icon" type="button" (click)="toggleVoice()" aria-label="Voice message">
                <app-icon name="mic" />
              </button>
              <button class="send-btn" type="button" (click)="sendText()" aria-label="Send">
                <app-icon name="send-msg" />
              </button>
            }
          </footer>
        </div>
      }

      @if (chatService.incomingCall(); as call) {
        <div class="call-banner">
          <p>Incoming {{ call.callType }} call from {{ call.startedByName || 'a teammate' }}</p>
          <button type="button" (click)="acceptCall()">Accept</button>
          <button class="secondary" type="button" (click)="declineCall()">Decline</button>
        </div>
      }
      @if (chatService.activeCall(); as call) {
        <div class="call-stage">
          <p>{{ call.callType }} call in progress</p>
          <video #localVideo autoplay muted playsinline></video>
          <video #remoteVideo autoplay playsinline></video>
          <button type="button" (click)="endCall()">End call</button>
        </div>
      }

      <app-modal [open]="composerOpen()" title="New chat" (close)="composerOpen.set(false)">
        @if (!authService.isStaff) {
          <p class="hint">Customers talk to Bank Support. Officers reply from the staff desk.</p>
          <button class="wa-row modal-row" type="button" (click)="openSupport()">
            <span class="wa-avatar support">S</span>
            <div class="wa-copy"><strong>Bank Support</strong><p>Talk to a bank officer</p></div>
          </button>
        } @else {
          <p class="hint">{{ composeMode === 'group' ? 'Add teammates, then create the group.' : 'Tap a name to open the chat.' }}</p>
          <div class="chip-row wrap">
            <button type="button" class="filter-chip" [class.active]="composeMode === 'direct'" (click)="composeMode = 'direct'">Direct</button>
            <button type="button" class="filter-chip" [class.active]="composeMode === 'group'" (click)="composeMode = 'group'">New group</button>
          </div>
          <label class="history-search">
            <app-icon name="search" />
            <input name="composeQuery" [(ngModel)]="composeQuery" placeholder="Search people" />
          </label>
          @if (composeMode === 'group') {
            <label>Group name<input name="groupTitle" [(ngModel)]="groupTitle" placeholder="Loans desk, Branch team" /></label>
          }
          @if (chatService.directoryLoading()) {
            <app-list-skeleton [count]="3" />
          } @else if (teamContacts().length) {
            <p class="eyebrow">Bank team</p>
            @for (user of teamContacts(); track user.userId) {
              <button class="wa-row modal-row" type="button" [disabled]="!!startingUserId()" (click)="composeMode === 'group' ? toggleGroupMember(user.userId) : startChatWith(user.userId)">
                <span class="wa-avatar-wrap">
                  <span class="wa-avatar">{{ initial(user.fullName || user.email) }}</span>
                  @if (chatService.isOnline(user.userId)) {
                    <span class="online-dot"></span>
                  }
                </span>
                <div class="wa-copy">
                  <strong>{{ user.fullName || user.email }}</strong>
                  <p>{{ labelForRole(user.role) }}</p>
                </div>
                @if (chatService.isOnline(user.userId)) {
                  <span class="active-badge">Active</span>
                }
                @if (composeMode === 'group') {
                  <span class="pick-mark" [class.on]="isGroupMember(user.userId)">{{ isGroupMember(user.userId) ? 'Added' : 'Add' }}</span>
                } @else if (startingUserId() === user.userId) {
                  <span class="pick-mark">Opening</span>
                }
              </button>
            }
          }
          @if (composeMode === 'direct' && customerContacts().length) {
            <p class="eyebrow">Customers</p>
            @for (user of customerContacts(); track user.userId) {
              <button class="wa-row modal-row" type="button" [disabled]="!!startingUserId()" (click)="startChatWith(user.userId)">
                <span class="wa-avatar-wrap">
                  <span class="wa-avatar">{{ initial(user.fullName || user.email) }}</span>
                  @if (chatService.isOnline(user.userId)) {
                    <span class="online-dot"></span>
                  }
                </span>
                <div class="wa-copy">
                  <strong>{{ user.fullName || user.email }}</strong>
                  <p>Customer</p>
                </div>
                @if (chatService.isOnline(user.userId)) {
                  <span class="active-badge">Active</span>
                }
                @if (startingUserId() === user.userId) {
                  <span class="pick-mark">Opening</span>
                }
              </button>
            }
          }
          @if (modalError()) { <p class="error">{{ modalError() }}</p> }
          @if (composeMode === 'group') {
            <button type="button" (click)="createGroup()" [disabled]="selectedGroupMembers.length === 0">Create group</button>
          }
        }
      </app-modal>
    </section>
  `
})
export class ChatPage implements OnInit {
  @ViewChild('localVideo') localVideo?: ElementRef<HTMLVideoElement>;
  @ViewChild('remoteVideo') remoteVideo?: ElementRef<HTMLVideoElement>;
  @ViewChild('messagePane') messagePane?: ElementRef<HTMLDivElement>;

  selectedConversation = signal<ChatConversation | null>(null);
  composerOpen = signal(false);
  searchText = '';
  composeQuery = '';
  composeMode: 'direct' | 'group' = 'direct';
  selectedUserId = '';
  selectedGroupMembers: string[] = [];
  groupTitle = '';
  modalError = signal('');
  draft = '';
  employeeName = '';
  employeeEmail = '';
  employeePassword = '';
  employeeRole = 'InternalEmployee';
  error = signal('');
  startingUserId = signal('');
  recording = signal(false);
  recClock = signal('0:00');
  readonly recBars = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
  private mediaRecorder?: MediaRecorder;
  private recordedChunks: Blob[] = [];
  private recordingStartedAt = 0;
  private recTimer = 0;
  private discardVoice = false;
  private threadPinned = false;

  constructor(
    readonly authService: AuthService,
    readonly chatService: ChatService,
    private readonly callService: CallService
  ) {
    effect(() => {
      this.chatService.messages();
      const loading = this.chatService.messagesLoading();
      this.selectedConversation();
      if (loading) {
        return;
      }
      const smooth = this.threadPinned;
      queueMicrotask(() => this.pinThread(smooth));
    });
    effect(() => {
      const pending = this.chatService.pendingOpen();
      if (!pending) {
        return;
      }
      this.chatService.pendingOpen.set(null);
      void this.selectConversation(pending);
    });
  }

  async ngOnInit() {
    await this.chatService.connect();
    this.chatService.loadDirectory(true);
    this.chatService.loadConversations(true);
    const conversation = history.state?.conversation as ChatConversation | undefined;
    if (conversation?.id) {
      void this.selectConversation(conversation);
    }
  }

  labelForRole(role: string) {
    if (role === 'InternalEmployee') return 'Internal employee';
    if (role === 'ExternalEmployee') return 'External employee';
    if (role === 'Admin') return 'Admin';
    return 'Customer';
  }

  memberSummary(conversation: ChatConversation) {
    return conversation.members
      .map((member) => `${member.displayName} (${this.labelForRole(member.role)})`)
      .join(', ');
  }

  visibleConversations() {
    const query = this.searchText.trim().toLowerCase();
    const items = this.chatService.conversations();
    if (!query) {
      return items;
    }
    return items.filter((conversation) => conversation.title.toLowerCase().includes(query) || conversation.lastMessage?.toLowerCase().includes(query));
  }

  openComposer() {
    this.composeMode = 'direct';
    this.composeQuery = '';
    this.groupTitle = '';
    this.selectedGroupMembers = [];
    this.modalError.set('');
    this.composerOpen.set(true);
  }

  staffDirectory() {
    const query = this.composeQuery.trim().toLowerCase();
    return this.chatService.directory().filter((user) => {
      if (user.userId === this.authService.userId()) {
        return false;
      }
      if (query && !`${user.fullName} ${user.email} ${user.role}`.toLowerCase().includes(query)) {
        return false;
      }
      return true;
    });
  }

  teamContacts() {
    return this.staffDirectory().filter((user) => user.role !== 'Customer');
  }

  customerContacts() {
    return this.staffDirectory().filter((user) => user.role === 'Customer');
  }

  isGroupMember(userId: string) {
    return this.selectedGroupMembers.includes(userId);
  }

  toggleGroupMember(userId: string) {
    if (this.isGroupMember(userId)) {
      this.selectedGroupMembers = this.selectedGroupMembers.filter((id) => id !== userId);
      return;
    }
    this.selectedGroupMembers = [...this.selectedGroupMembers, userId];
  }

  initial(value: string) {
    return (value || 'C').charAt(0).toUpperCase();
  }

  previewLast(lastMessage: string, fallback: string) {
    if (!lastMessage) {
      return fallback;
    }
    if (lastMessage.toLowerCase().includes('.webm') || lastMessage.toLowerCase().includes('voice')) {
      return 'Voice message';
    }
    return lastMessage;
  }

  shortTime(value?: string) {
    if (!value) {
      return '';
    }
    return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  startChatWith(userId: string) {
    if (this.startingUserId()) {
      return;
    }
    this.selectedUserId = userId;
    this.startingUserId.set(userId);
    this.modalError.set('');
    this.chatService.startDirect(userId).subscribe({
      next: (conversation) => {
        this.startingUserId.set('');
        this.composerOpen.set(false);
        this.afterOpen(conversation);
      },
      error: (error) => {
        this.startingUserId.set('');
        this.modalError.set(readErrorMessage(error, 'Unable to start the chat.'));
      }
    });
  }

  openDirect() {
    if (!this.selectedUserId) {
      return;
    }
    this.startChatWith(this.selectedUserId);
  }

  createGroup() {
    if (!this.groupTitle.trim()) {
      this.modalError.set('Give the group a name.');
      return;
    }
    if (this.selectedGroupMembers.length === 0) {
      this.modalError.set('Add at least one teammate.');
      return;
    }

    this.modalError.set('');
    this.chatService.startGroup(this.groupTitle.trim(), this.selectedGroupMembers).subscribe({
      next: (conversation) => {
        this.composerOpen.set(false);
        this.afterOpen(conversation);
      },
      error: (error) => this.modalError.set(readErrorMessage(error, 'Unable to create the group.'))
    });
  }

  openSupport() {
    this.composerOpen.set(false);
    this.chatService.startSupport().subscribe({
      next: (conversation) => this.afterOpen(conversation),
      error: (error) => this.error.set(readErrorMessage(error, 'Unable to open support.'))
    });
  }

  closeThread() {
    this.threadPinned = false;
    this.selectedConversation.set(null);
    this.chatService.closeConversation();
  }

  async selectConversation(conversation: ChatConversation) {
    this.threadPinned = false;
    this.selectedConversation.set({ ...conversation, unreadCount: 0 });
    await this.chatService.openConversation(conversation);
  }

  otherMembers(conversation: ChatConversation) {
    return conversation.members.filter((member) => member.userId !== this.authService.userId());
  }

  isConversationOnline(conversation: ChatConversation) {
    return this.otherMembers(conversation).some((member) => this.chatService.isOnline(member.userId));
  }

  isSupportOnline() {
    return this.chatService.directory().some((user) => user.role !== 'Customer' && this.chatService.isOnline(user.userId));
  }

  isMineLast(conversation: ChatConversation) {
    return !!conversation.lastMessageSenderUserId && conversation.lastMessageSenderUserId.toLowerCase() === this.authService.userId().toLowerCase();
  }

  threadPresence(conversation: ChatConversation) {
    const onlineCount = this.otherMembers(conversation).filter((member) => this.chatService.isOnline(member.userId)).length;
    if (onlineCount === 0) {
      return 'offline';
    }
    if (conversation.conversationType === 'Group' && onlineCount > 1) {
      return `${onlineCount} online`;
    }
    return 'online';
  }

  async sendText() {
    const conversation = this.selectedConversation();
    if (!conversation || !this.draft.trim()) {
      return;
    }
    await this.chatService.sendText(conversation.id, this.draft);
    this.draft = '';
  }

  onTyping() {
    const conversation = this.selectedConversation();
    if (conversation) {
      void this.chatService.notifyTyping(conversation.id);
    }
  }

  onFileSelected(event: Event) {
    const conversation = this.selectedConversation();
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!conversation || !file) {
      return;
    }
    this.chatService.uploadFile(conversation.id, file).subscribe({
      next: (message) => this.chatService.upsertMessage(message)
    });
    input.value = '';
  }

  cancelVoice() {
    this.discardVoice = true;
    this.stopRecording();
  }

  async toggleVoice() {
    const conversation = this.selectedConversation();
    if (!conversation) {
      return;
    }
    if (this.recording()) {
      this.stopRecording();
      return;
    }
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    this.recordedChunks = [];
    this.discardVoice = false;
    this.recordingStartedAt = Date.now();
    this.recClock.set('0:00');
    this.mediaRecorder = new MediaRecorder(stream);
    this.mediaRecorder.ondataavailable = (event) => {
      if (event.data.size > 0) {
        this.recordedChunks.push(event.data);
      }
    };
    this.mediaRecorder.onstop = () => {
      stream.getTracks().forEach((track) => track.stop());
      this.clearRecTimer();
      if (this.discardVoice) {
        this.discardVoice = false;
        return;
      }
      const blob = new Blob(this.recordedChunks, { type: 'audio/webm' });
      const seconds = Math.max(1, Math.round((Date.now() - this.recordingStartedAt) / 1000));
      this.chatService.uploadVoice(conversation.id, blob, seconds).subscribe({
        next: (message) => this.chatService.upsertMessage(message)
      });
    };
    this.mediaRecorder.start();
    this.recording.set(true);
    this.recTimer = window.setInterval(() => {
      const seconds = Math.max(0, Math.round((Date.now() - this.recordingStartedAt) / 1000));
      this.recClock.set(`${Math.floor(seconds / 60)}:${(seconds % 60).toString().padStart(2, '0')}`);
    }, 250);
  }

  private stopRecording() {
    this.mediaRecorder?.stop();
    this.recording.set(false);
    this.clearRecTimer();
  }

  private clearRecTimer() {
    window.clearInterval(this.recTimer);
    this.recTimer = 0;
  }

  async startCall(callType: 'Voice' | 'Video') {
    const conversation = this.selectedConversation();
    if (!conversation) {
      return;
    }
    await this.callService.beginOutgoing(conversation.id, callType);
    this.bindVideos();
  }

  async acceptCall() {
    const call = this.chatService.incomingCall();
    if (!call) {
      return;
    }
    await this.callService.acceptIncoming(call.id, call.callType);
    this.chatService.incomingCall.set(null);
    this.bindVideos();
  }

  async declineCall() {
    const call = this.chatService.incomingCall();
    if (call) {
      await this.chatService.declineCall(call.id);
    }
  }

  async endCall() {
    await this.callService.hangUp();
  }

  createEmployee() {
    this.chatService
      .createEmployee({
        email: this.employeeEmail,
        password: this.employeePassword,
        fullName: this.employeeName,
        role: this.employeeRole
      })
      .subscribe({
        next: () => {
          this.employeeName = '';
          this.employeeEmail = '';
          this.employeePassword = '';
          this.chatService.loadDirectory();
        },
        error: (error) => this.error.set(readErrorMessage(error, 'Unable to create the employee.'))
      });
  }

  private pinThread(smooth = false) {
    const pane = this.messagePane?.nativeElement;
    if (!pane) {
      return;
    }
    const top = pane.scrollHeight;
    pane.scrollTo({ top, behavior: smooth ? 'smooth' : 'auto' });
    requestAnimationFrame(() => {
      pane.scrollTop = pane.scrollHeight;
      this.threadPinned = true;
    });
  }

  private afterOpen(conversation: ChatConversation) {
    this.error.set('');
    this.chatService.loadConversations();
    void this.selectConversation(conversation);
  }

  private bindVideos() {
    setTimeout(() => {
      if (this.localVideo && this.callService.localStream) {
        this.localVideo.nativeElement.srcObject = this.callService.localStream;
      }
      if (this.remoteVideo && this.callService.remoteStream) {
        this.remoteVideo.nativeElement.srcObject = this.callService.remoteStream;
      }
    }, 300);
  }
}
