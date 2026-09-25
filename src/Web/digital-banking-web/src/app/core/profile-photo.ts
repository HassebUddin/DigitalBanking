export function photoKey(userId: string) {
  return `digitalBanking.photo.${userId}`;
}

export function readProfilePhoto(userId: string) {
  return localStorage.getItem(photoKey(userId)) ?? '';
}

export function saveProfilePhoto(userId: string, dataUrl: string) {
  localStorage.setItem(photoKey(userId), dataUrl);
}
