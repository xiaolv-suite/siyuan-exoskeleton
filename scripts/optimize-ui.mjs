import fs from 'fs';
import path from 'path';

const panelPath = 'D:/思源插件/思源小驴桌面助手/assets/panel.html';
let content = fs.readFileSync(panelPath, 'utf8');

console.log('Original panel.html size:', content.length, 'bytes');

// 1. Remove STORE_ITEMS and STORE_PAYLOADS
content = content.replace(/const STORE_ITEMS = \[[\s\S]*?\];\s*/g, '');
content = content.replace(/const STORE_PAYLOADS = \{[\s\S]*?\};\s*/g, '');

// 2. Remove appstore-modal HTML
content = content.replace(/<!-- =+[\s\S]*?APP STORE MODAL[\s\S]*?<\/div>\s*<\/div>\s*<\/div>/g, '');

// 3. Remove store button from header
content = content.replace(/<button class="theme-toggle-btn store-btn"[\s\S]*?<\/button>/g, '');

// 4. Remove customize/edit-mode button from header
content = content.replace(/<button class="theme-toggle-btn" id="customize-btn"[\s\S]*?<\/button>/g, '');

// 5. Remove layout-switcher from controls bar
content = content.replace(/<div class="layout-switcher">[\s\S]*?<\/div>/g, '');

// 6. Remove edit-toolbar
content = content.replace(/<div class="edit-toolbar" id="edit-toolbar">[\s\S]*?<\/div>/g, '');

// 7. Remove Section 4 (Quicker Suite Sharing) from preferences modal
content = content.replace(/<!-- Section 4: Quicker Suite Sharing[\s\S]*?<!-- Section 5/g, '<!-- Section 5');

// 8. Remove all store functions from script
const storeFuncPattern = /let storeSelectedCodes = [\s\S]*?function closeAppStoreOnBg\(e\) \{[\s\S]*?\}/g;
content = content.replace(storeFuncPattern, '');

// 9. Clean up any remaining layoutMode switcher functions
content = content.replace(/function setLayoutMode\(m\) \{[\s\S]*?function syncLayoutButtons\(\) \{[\s\S]*?\}/g, '');

console.log('After pruning Quicker bloat, size:', content.length, 'bytes');
fs.writeFileSync(panelPath, content, 'utf8');
console.log('Pruning successfully applied!');
