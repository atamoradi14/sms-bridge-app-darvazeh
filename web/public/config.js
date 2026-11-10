// Overridden at deploy time via ConfigMap mount in Kubernetes.
// Empty string = same origin (Ingress routes /api → API).
window.__FIELDSENSE_API_BASE__ = window.__FIELDSENSE_API_BASE__ || "";
