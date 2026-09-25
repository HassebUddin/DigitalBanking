import { Component, ElementRef, OnInit, ViewChild, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../core/auth.service';
import { CallService } from '../core/call.service';
import { ChatService } from '../core/chat.service';
import { ChatConversation } from '../core/chat.models';
import { readErrorMessage } from '../core/http-error';
import { AppModal } from '../ui/modal';
import { AppIcon } from '../ui/icon';

@Component({
  selector: 'app-chat-page',
  imports: [FormsModule, AppModal, AppIcon],
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
                <span class="wa-avatar support">S</span>
                <div class="wa-copy">
                  <div class="wa-top">
                    <strong>Bank Support</strong>
                    <small>Now</small>
                  </div>
                  <p>Need help with your account?</p>
                </div>
              </button>
            }
            @for (conversation of visibleConversations(); track conversation.id) {
              <button class="wa-row" type="button" (click)="selectConversation(conversation)">
                <span class="wa-avatar">{{ initial(conversation.title) }}</span>
                <div class="wa-copy">
                  <div class="wa-top">
                    <strong>{{ conversation.title }}</strong>
                    <small>{{ shortTime(conversation.lastMessageAtUtc || conversation.createdAtUtc) }}</small>
                  </div>
                  <p>{{ previewLast(conversation.lastMessage, conversation.conversationType) }}</p>
                </div>
              </button>
            }
          </div>
        </div>
      } @else {
        <div class="thread">
          <header class="thread-head">
            <button class="round-btn" type="button" (click)="closeThread()" aria-label="Back">
              <app-icon name="back" />
            </button>
            <span class="wa-avatar sm">{{ initial(selectedConversation()?.title || 'C') }}</span>
            <div class="thread-who">
              <strong>{{ selectedConversation()?.title }}</strong>
              @if (chatService.typingName()) {
                <small>typing...</small>
              } @else {
                <small>{{ selectedConversation()?.conversationType }}</small>
              }
            </div>
            <button class="round-btn" type="button" (click)="startCall('Video')" aria-label="Video call">
              <app-icon name="video" />
            </button>
            <button class="round-btn" type="button" (click)="startCall('Voice')" aria-label="Voice call">
              <app-icon name="phone" />
            </button>
          </header>
          <div class="message-list">
            @for (message of chatService.messages(); track message.id) {
              <article class="bubble" [class.mine]="message.senderUserId === authService.userId()">
                @if (message.messageType === 'Text' || message.messageType === 'Call' || message.messageType === 'System') {
                  <p>{{ message.body }}</p>
                }
                @if (message.messageType === 'File') {
                  <a [href]="chatService.fileUrl(message.fileUrl)" target="_blank">{{ message.fileName }}</a>
                }
                @if (message.messageType === 'Voice') {
                  <audio [src]="chatService.fileUrl(message.fileUrl)" controls></audio>
                }
                <small>{{ shortTime(message.createdAtUtc) }}</small>
              </article>
            }
          </div>
          <footer class="composer">
            <input #fileInput type="file" hidden (change)="onFileSelected($event)" />
            <button class="composer-icon" type="button" (click)="fileInput.click()" aria-label="Attach file">
              <app-icon name="attach" />
            </button>
            <input name="draft" [(ngModel)]="draft" (ngModelChange)="onTyping()" placeholder="Type a message" />
            <button class="composer-icon" type="button" [class.recording]="recording()" (click)="toggleVoice()" aria-label="Voice message">
              <app-icon name="mic" />
            </button>
            <button class="send-btn" type="button" (click)="sendText()" aria-label="Send">
              <app-icon name="send-msg" />
            </button>
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
          @if (teamContacts().length) {
            <p class="eyebrow">Bank team</p>
            @for (user of teamContacts(); track user.userId) {
              <button class="wa-row modal-row" type="button" (click)="composeMode === 'group' ? toggleGroupMember(user.userId) : startChatWith(user.userId)">
                <span class="wa-avatar">{{ initial(user.fullName || user.email) }}</span>
                <div class="wa-copy">
                  <strong>{{ user.fullName || user.email }}</strong>
                  <p>{{ labelForRole(user.role) }}</p>
                </div>
                @if (composeMode === 'group') {
                  <span class="pick-mark" [class.on]="isGroupMember(user.userId)">{{ isGroupMember(user.userId) ? 'Added' : 'Add' }}</span>
                }
              </button>
            }
          }
          @if (composeMode === 'direct' && customerContacts().length) {
            <p class="eyebrow">Customers</p>
            @for (user of customerContacts(); track user.userId) {
              <button class="wa-row modal-row" type="button" (click)="startChatWith(user.userId)">
                <span class="wa-avatar">{{ initial(user.fullName || user.email) }}</span>
                <div class="wa-copy">
                  <strong>{{ user.fullName || user.email }}</strong>
                  <p>Customer</p>
                </div>
              </button>
            }
          }
          @if (composeMode === 'group') {
            @if (modalError()) { <p class="error">{{ modalError() }}</p> }
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
  recording = signal(false);
  private mediaRecorder?: MediaRecorder;
  private recordedChunks: Blob[] = [];
  private recordingStartedAt = 0;

  constructor(
    readonly authService: AuthService,
    readonly chatService: ChatService,
    private readonly callService: CallService
  ) {}

  async ngOnInit() {
    await this.chatService.connect();
    this.chatService.loadDirectory();
    this.chatService.loadConversations();
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
    this.selectedUserId = userId;
    this.composerOpen.set(false);
    this.openDirect();
  }

  openDirect() {
    if (!this.selectedUserId) {
      return;
    }
    this.chatService.startDirect(this.selectedUserId).subscribe({
      next: (conversation) => this.afterOpen(conversation),
      error: (error) => this.error.set(readErrorMessage(error, 'Unable to start the chat.'))
    });
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
    this.selectedConversation.set(null);
  }

  async selectConversation(conversation: ChatConversation) {
    this.selectedConversation.set(conversation);
    await this.chatService.openConversation(conversation);
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
    this.chatService.uploadFile(conversation.id, file).subscribe();
    input.value = '';
  }

  async toggleVoice() {
    const conversation = this.selectedConversation();
    if (!conversation) {
      return;
    }
    if (this.recording()) {
      this.mediaRecorder?.stop();
      this.recording.set(false);
      return;
    }
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    this.recordedChunks = [];
    this.recordingStartedAt = Date.now();
    this.mediaRecorder = new MediaRecorder(stream);
    this.mediaRecorder.ondataavailable = (event) => {
      if (event.data.size > 0) {
        this.recordedChunks.push(event.data);
      }
    };
    this.mediaRecorder.onstop = () => {
      stream.getTracks().forEach((track) => track.stop());
      const blob = new Blob(this.recordedChunks, { type: 'audio/webm' });
      const seconds = Math.max(1, Math.round((Date.now() - this.recordingStartedAt) / 1000));
      this.chatService.uploadVoice(conversation.id, blob, seconds).subscribe();
    };
    this.mediaRecorder.start();
    this.recording.set(true);
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
