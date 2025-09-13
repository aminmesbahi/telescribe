# 🔭 Telescribe

**Telescribe** is a flexible .NET console application that helps you manage content from your Telegram channel—whether you want to archive it, analyze engagement, enhance it with AI, publish it on a website, or push it to WordPress.

It’s built for creators who want more control over their Telegram content, turning messages into searchable, structured, and beautifully presented posts—across platforms.

## Summary

Telescribe helps you turn your Telegram channel into a powerful content hub. It exports posts (including media and metadata), enhances them using AI (titles, hashtags, summaries), and supports publishing workflows—from generating static websites to posting directly on WordPress. With built-in analytics and a flexible template system, Telescribe makes it easy to preserve, process, and present your content—on your terms.

## Features

### 📥 **Telegram Export**
- Export all posts from any Telegram channel you have access to
- Download media files (images, videos, documents, audio)
- Incremental updates - sync only new posts since last export
- Collects channel posts with views, reactions, and forward counts
- Markdown format export for easy processing

### 🤖 **AI Content Processing** 
- **Multi-Provider Support**: OpenAI, DeepSeek, Ollama
- **Smart Title Generation**: Automatically generate engaging titles for posts
- **Hashtag Extraction**: Extract relevant hashtags from content
- **Content Enhancement**: Process posts to improve readability and SEO
- **Batch Processing**: Handle large volumes of posts efficiently

### 🏗️ **Static Website Generation**
- Create beautiful, responsive static websites from exported content
- **Multi-Language Templates**: English (`en`) and Persian/Farsi (`fa`) support
- **Customizable Design**: Modern, mobile-friendly templates
- **SEO Optimized**: Clean URLs, meta tags, and structured content
- **Fast Loading**: Static files for optimal performance

### 📊 **Analytics & Reporting**
- Comprehensive analytics reports with engagement metrics
- **Template-Based Reports**: Beautiful HTML reports using template system
- **Key Metrics**: Views, reactions, forwards, and engagement analysis
- **Visual Charts**: Modern dashboard-style analytics interface
- **Export Summaries**: Detailed export statistics and performance data

### 🌐 **WordPress Integration**
- Direct publishing to WordPress sites
- **Media Upload**: Automatic media file uploads to WordPress
- **Category Management**: Automatic category creation and assignment
- **Post Mapping**: Track published posts to avoid duplicates
- **Bulk Operations**: Efficiently handle large content volumes

### ⚙️ **Advanced Features**
- **Template System**: Flexible template engine for customization
- **Media Handling**: Support for all Telegram media types

## Requirements

### System Requirements
- **.NET 10.0** runtime or higher

### Telegram API Setup
- **Telegram API credentials** from [https://my.telegram.org/](https://my.telegram.org/)
  - API ID
  - API Hash
  - Phone number (international format)
- **Channel access** (must be member of target channel)

### Optional Dependencies
- **AI Services** (choose one):
  - OpenAI API key for GPT models
  - DeepSeek API key for cost-effective processing
  - Ollama local installation for offline processing
- **WordPress site** with application password for publishing

## How to Run

### 🔨 Coming soon: As .NET tool!

### 1. **Clone and Build**
```bash
git clone https://github.com/aminmesbahi/telescribe.git
cd telescribe/src
dotnet build
```

### 2. **Configuration Setup**
Create `appsettings.json` in `Telescribe.Console` directory:

```json
{
  "TelegramConfig": {
    "PhoneNumber": "+1234567890",
    "ChannelId": "@yourchannel",
    "ApiId": 12345678,
    "ApiHash": "your-api-hash",
    "SummaryCharacterCount": 200,
    "LLM": {
      "EnableProcessing": true,
      "Provider": "OpenAI",
      "ApiKey": "your-api-key",
      "ModelName": "gpt-4",
      "GenerateTitle": true,
      "ExtractHashtags": true,
      "MaxHashtags": 5,
      "Language": "English"
    },
    "WordPress": {
      "BaseUrl": "https://yoursite.com",
      "Username": "your-username",
      "Password": "your-app-password",
      "EnableUploads": true,
      "DefaultCategoryId": "Telegram Posts"
    },
    "StaticSite": {
      "SiteTitle": "My Telegram Archive",
      "Subtitle": "Content from my Telegram channel",
      "TemplateName": "en",
      "MaxPostsInIndex": 50,
      "OpenBrowserAfterGeneration": true
    }
  }
}
```

### 3. **Run the Application**

**Interactive Mode:**
```bash
cd Telescribe.Console
dotnet run
```

**Command Line Mode:**
```bash
# Generate static website
dotnet run --project Telescribe.Console static

# Generate analytics reports
dotnet run --project Telescribe.Console reports
```

### 4. **Follow the Menu**
1. **Export Channel Posts** - Initial full export
2. **Update Existing Posts** - Incremental sync
3. **AI Content Processing** - Coming Soon!
4. **WordPress Integration** - Coming Soon!
5. **Analytics Reports** - Generate insights
6. **Static Website Builder** - Create website
7. **Exit Application**

## Roadmap

### 🚧 **Version 1.0** (In Development)
- [ ] **Finalize features**: Complete AI and Wordpress
- [ ] **Stable Release**: Fix issues, make it stable

### 🚧 **Version 2.0** (Some day!)
- [ ] **Real-time Sync**: Live monitoring and auto-sync
- [ ] **Advanced AI Features**: Content categorization and summarization
- [ ] **Database Support**: DB integration
- [ ] **REST API**: Web API for programmatic access
- [ ] **Web Dashboard**: Browser-based management interface

### 🎯 **Version 3.0** (Maybe!)
- [ ] **Multi-Channel Support**: Handle multiple channels simultaneously
- [ ] **Content Scheduling**: Automated publishing workflows
- [ ] **Analytics Dashboard**: Advanced metrics and insights
- [ ] **Plugin System**: Extensible architecture for custom features
- [ ] **Cloud Deployment**: Docker containers and cloud-ready deployment

### 🔮 **Future Enhancements**
- [ ] **Social Media Integration**: Twitter, LinkedIn, Facebook publishing
- [ ] **Machine Learning**: Predictive analytics and content recommendations based on users' behaviour

## Known Issues

### Current Limitations
- **LLM Processing**: Currently limited and it is in development
- **WordPress Upload**: Menu option exists but full integration is in development
- **Template Customization**: Limited to predefined templates (extensibility planned)

### Performance Considerations
- **API Rate Limits**: Telegram API has built-in rate limiting
- **AI Processing**: Can be slow for large batches (use processing delays)

### Workarounds
- **Large Channels**: Use incremental updates rather than full re-exports
- **AI Timeouts**: Increase timeout settings for slow AI responses
- **Template Issues**: Check template file permissions and paths
- **Configuration Errors**: Validate JSON syntax and required fields

---

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit issues, feature requests, or pull requests.

## Support

For issues and questions:
- **GitHub Issues**: [Create an issue](https://github.com/aminmesbahi/telescribe/issues)