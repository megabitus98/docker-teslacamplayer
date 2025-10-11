// src/apm.ts
import { init as initApm } from '@elastic/apm-rum';

declare global {
  interface Window {
    __APM__?: {
      serviceName?: string;
      serverUrl?: string;
      serviceVersion?: string;
      environment?: string;
      distributedTracingOrigins?: string[]; // optional CSV via Docker build args
    };
    apm?: ReturnType<typeof initApm>;
  }
}

const cfg = window.__APM__ || {};

window.apm = initApm({
  serviceName: cfg.serviceName || 'teslacamplayer-client',
  serverUrl: cfg.serverUrl || 'http://localhost:8200',
  serviceVersion: cfg.serviceVersion || 'prod',
  environment:
    cfg.environment || (location.hostname === 'localhost' ? 'development' : 'production'),
  // add your API origins here if your backend isn’t the same origin
  distributedTracingOrigins: cfg.distributedTracingOrigins || [location.origin],
  breakdownMetrics: true
});

// no exports needed; we just want it to run on load
export {};