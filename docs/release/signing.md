# 发布签名与签名后复验（SignPath）

> 状态：**准备完成，尚未提交 SignPath 申请/尚未签名** ｜ 日期：2026-10-29
>
> 官方依据（2026-10-29 核对）：
> - https://docs.signpath.io/trusted-build-systems/github
> - https://docs.signpath.io/origin-verification
> - https://signpath.org

## 一、当前状态

- Apage 当前产物为 **未签名** 的 `artifacts\portable\Apage.Portable.exe`（`signatureStatus=NotSigned`）。
- `build\verify-signature.ps1` 已可记录签名状态、校验 SHA-256，并在 `-RequireValid` 下要求 Authenticode 有效。
- 仓库不包含任何 SignPath token、组织 ID 或证书。
- 不创建“看起来已签名”的假工作流；申请、项目绑定和密钥由人工完成后再启用签名 job。

## 二、申请前必须完成

1. 以 Apage 的公开仓库提交 Open Source signing 申请；确认仓库许可证、README、构建入口可公开审计。
2. 在 SignPath 项目中绑定 GitHub 作为 Trusted Build System，并开启 Origin Verification。
3. 配置 Source code repository URL 与允许的分支（建议 `main` / `release/*`）。
4. 在 SignPath 创建 Artifact Configuration：
   - 若 GitHub 上传时使用 `archive: false`，根元素按单个文件配置；
   - 若使用默认 ZIP artifact，根元素必须是 `<zip-file>`；
   - 变量与 `package.ps1` manifest 的版本、SHA-256 对齐。
5. 创建 Signing Policy，明确要求 Origin Verification，且仅允许发布分支使用发布证书。
6. 将以下值放入 GitHub Environment/Secrets，不写入仓库：
   - `SIGNPATH_API_TOKEN`
   - `organization-id`
   - `project-slug`
   - `signing-policy-slug`
   - `artifact-configuration-slug`（如不使用默认配置）

## 三、签名工作流骨架

以下是启用签名时使用的结构；参数名称来自 SignPath 官方 GitHub 文档（action `v3`）。当前不提交为可执行 workflow，避免缺少 Secrets 时产生红叉。

```yaml
- name: Upload unsigned EXE
  id: upload-unsigned
  uses: actions/upload-artifact@v4
  with:
    name: Apage-unsigned-for-signing
    path: artifacts/portable/Apage.Portable.exe
    archive: false
    retention-days: 1

- name: Submit SignPath signing request
  uses: signpath/github-action-submit-signing-request@v3
  with:
    api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
    organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
    project-slug: ${{ vars.SIGNPATH_PROJECT_SLUG }}
    signing-policy-slug: ${{ vars.SIGNPATH_SIGNING_POLICY_SLUG }}
    artifact-configuration-slug: ${{ vars.SIGNPATH_ARTIFACT_CONFIGURATION_SLUG }}
    github-artifact-id: ${{ steps.upload-unsigned.outputs.artifact-id }}
    wait-for-completion: true
    output-artifact-directory: artifacts/signed
    skip-decompress: true
```

若使用默认 ZIP artifact，删除 `archive: false` 与 `skip-decompress: true`，并按 SignPath 要求把 Artifact Configuration 根元素设为 `<zip-file>`。

## 四、签名后验收（S4 必做）

签名会改变文件字节和 SHA-256，因此必须对 **签名后的同一 EXE** 重跑，而不是复用未签名 manifest：

```powershell
# 1. 记录签名状态 + 新 SHA-256
build\verify-signature.ps1 `
  -ExePath artifacts\signed\Apage.Portable.exe `
  -RequireValid `
  -JsonPath artifacts\signed\signature.json

# 2. 五信号 smoke
build\smoke.ps1 -ExePath artifacts\signed\Apage.Portable.exe

# 3. 签名后启动复测（参考机阈值 1000ms）
build\startup-bench.ps1 `
  -ExePath artifacts\signed\Apage.Portable.exe `
  -Iterations 5 `
  -OutputDirectory artifacts\signed-startup `
  -WarmMedianLimitMs 1000
```

验收记录必须包含：

- `signatureStatus=Valid`、签名者 Subject、时间戳状态；
- 签名后 SHA-256（发布页与更新说明使用此值，不使用未签名 hash）；
- 单文件数 / 字节 < 5,000,000；
- 五信号 smoke 结果；
- 首启与热启 ContentRendered；
- 签名后版本号 `0.1.0.0` / `0.1.0-dev`（或实际发布版本）。

## 五、禁止事项

- 不在签名后重新构建或替换 EXE；
- 不把 `signtool` 的失败忽略为“稍后补签”；
- 不将 API token 写入脚本、manifest、日志或提交历史；
- 不宣称已签名，直到 `verify-signature.ps1 -RequireValid` 返回 0。
