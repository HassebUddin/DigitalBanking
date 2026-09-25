export interface DirectoryUser {
  userId: string;
  email: string;
  fullName: string;
  role: string;
}

export interface ChatMember {
  userId: string;
  displayName: string;
  role: string;
}

export interface ChatConversation {
  id: string;
  title: string;
  conversationType: string;
  createdAtUtc: string;
  lastMessage: string;
  lastMessageAtUtc?: string;
  unreadCount: number;
  members: ChatMember[];
}

export interface ChatMessage {
  id: string;
  conversationId: string;
  senderUserId: string;
  senderName: string;
  messageType: string;
  body: string;
  fileName?: string;
  fileUrl?: string;
  contentType?: string;
  durationSeconds?: number;
  createdAtUtc: string;
}

export interface ChatCall {
  id: string;
  conversationId: string;
  startedByUserId: string;
  startedByName: string;
  callType: string;
  status: string;
}
