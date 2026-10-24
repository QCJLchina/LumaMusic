import { describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

// test/unit/electron/linuxDesktopIntegration.test.ts
// Tests Linux desktop entry metadata, WM_CLASS / app_id alignment, and packaging configuration.

describe('linuxDesktopIntegration', () => {
  const rootDir = path.resolve(__dirname, '../../../');
  const packageJsonPath = path.join(rootDir, 'package.json');
  const mainCjsPath = path.join(rootDir, 'electron/main.cjs');
  const linuxDesktopTemplatePath = path.join(rootDir, 'packaging/linux/lumamusic.desktop');
  const aurDesktopPath = path.join(rootDir, 'packaging/aur/lumamusic-bin/lumamusic.desktop');
  const aurPkgbuildPath = path.join(rootDir, 'packaging/aur/lumamusic-bin/PKGBUILD');
  const aurSrcinfoPath = path.join(rootDir, 'packaging/aur/lumamusic-bin/.SRCINFO');

  it('declares desktopName and syncDesktopName in package.json matching executableName', () => {
    const pkg = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));

    expect(pkg.desktopName).toBe('lumamusic.desktop');
    expect(pkg.build?.linux?.executableName).toBe('lumamusic');
    expect(pkg.build?.linux?.syncDesktopName).toBe(true);
    expect(pkg.build?.linux?.desktop?.entry?.StartupWMClass).toBe('lumamusic');
  });

  it('leaves Linux desktop identity to package.json desktopName instead of electron/main.cjs', () => {
    // Electron applies package.json desktopName at startup; a second setDesktopName call would drift from it.
    const mainContent = fs.readFileSync(mainCjsPath, 'utf8');

    expect(mainContent).not.toContain('app.setDesktopName(');
  });

  it('aligns StartupWMClass to lumamusic in portable linux desktop entry', () => {
    const content = fs.readFileSync(linuxDesktopTemplatePath, 'utf8');

    expect(content).toMatch(/^StartupWMClass=lumamusic$/m);
  });

  it('aligns StartupWMClass and verifies sha256 checksums in AUR package', () => {
    const desktopContent = fs.readFileSync(aurDesktopPath, 'utf8');
    expect(desktopContent).toMatch(/^StartupWMClass=lumamusic$/m);

    const desktopHash = crypto.createHash('sha256').update(desktopContent).digest('hex');
    const pkgbuildContent = fs.readFileSync(aurPkgbuildPath, 'utf8');
    const srcinfoContent = fs.readFileSync(aurSrcinfoPath, 'utf8');

    expect(pkgbuildContent).toContain(desktopHash);
    expect(srcinfoContent).toContain(desktopHash);
  });
});
