// The messaging implementations realize the application-layer contracts (provisioners, dispatcher,
// launcher, resolver, metrics, options) defined in DiffPdf.Application.Abstractions. Imported project-wide
// so the many consumers (handlers, executors, services, DI registrations) need no per-file using.
global using DiffPdf.Application.Abstractions;
