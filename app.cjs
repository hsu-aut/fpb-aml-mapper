// CommonJS entry point for Phusion Passenger (Plesk)
if (typeof PhusionPassenger !== 'undefined') {
  PhusionPassenger.configure({ autoInstall: false });
}
import('./server.js');
