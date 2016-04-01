﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Soundcloud_Playlist_Downloader.JsonPoco;
using Soundcloud_Playlist_Downloader.Properties;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Soundcloud_Playlist_Downloader
{
    class PlaylistSync
    {
        public bool IsError { get; protected set; }

        public enum DownloadMode { Playlist, Favorites, Artist };

        public int SongsToDownload { get; private set; }
        public int SongsDownloaded { get; private set; }

        private readonly object _songsDownloadedLock = new object();
        private static readonly object _writeManifestLock = new object();
        protected static object WritePlaylistLock = new object();

        public bool IsActive { get; set; }

        public PlaylistSync()
        {
            SongsToDownload = 0;
            SongsDownloaded = 0;
            ResetProgress();
        }

        private void VerifyParameters(Dictionary<string, string> parameters)
        {
            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Value))
                {
                    IsError = true;
                    throw new Exception($"{parameter.Key} must be specified");
                }
            }
        }

        internal void Synchronize(string url, DownloadMode mode, string directory, string clientId)
        {
            VerifyParameters(
                new Dictionary<string, string>()
                {
                    {"URL", url},
                    {"Directory", directory},
                    {"Client ID", clientId}
                }
            );
            ResetProgress();

            string apiURL = null;

            switch (mode)
            {
                case DownloadMode.Playlist:
                    // determine whether it is an api url or a normal url. if it is a normal url, get the api url from it
                    // and then call SynchronizeFromPlaylistAPIUrl. Otherwise just call that method directly
                    
                    if (!url.Contains("api.soundcloud.com"))
                    {
                        apiURL = DetermineApiUrlForNormalUrl(url, clientId,"playlists");
                    }
                    else 
                    {
                        apiURL = url;
                    }
                    SynchronizeFromPlaylistApiUrl(apiURL, clientId, directory);
                    break;
                case DownloadMode.Favorites:
                    // get the username from the url and then call SynchronizeFromProfile
                    string username = ParseUserIdFromProfileUrl(url);
                    SynchronizeFromProfile(username, clientId, directory);
                    break;
                case DownloadMode.Artist:
                    
                    if (!url.Contains("api.soundcloud.com"))
                    {
                        apiURL = DetermineApiUrlForNormalUrl(url, clientId,"tracks");
                    }
                    else 
                    {
                        apiURL = url;
                    }
                    SynchronizeFromArtistUrl(apiURL, clientId, directory);
                    break;
                default:
                    IsError = true;
                    throw new NotImplementedException("Unknown download mode");
            }
        }

        private string DetermineApiUrlForNormalUrl(string url, string clientId,string resulttype)
        {

            // parse the username from the url
            string username = ParseUserIdFromProfileUrl(url);
            string playlistName = null;
            try
            {
                // parse the playlist name from the url
                string startingPoint = "/sets/";
                int startingIndex = url.IndexOf(startingPoint, StringComparison.Ordinal) + startingPoint.Length;
                int endingIndex = url.Substring(startingIndex).Contains("/") ?
                    url.Substring(startingIndex).IndexOf("/", StringComparison.Ordinal) + startingIndex :
                    url.Length;
                playlistName = url.Substring(startingIndex, endingIndex - startingIndex);
            }
            catch (Exception e)
            {
                IsError = true;
                throw new Exception("Invalid playlist url: " + e.Message);
            }

            // hit the users/username/playlists endpoint and match the playlist on the permalink
            string userUrl = "https://api.soundcloud.com/users/" + username + "/" + resulttype;

            if (resulttype == "tracks")
            {
                return userUrl;
            }

            return "https://api.soundcloud.com/playlists/" +
                RetrievePlaylistId(userUrl, playlistName, clientId);
        }

        private string RetrievePlaylistId(string userApiUrl, string playlistName, string clientId)
        {

            // grab the xml from the url, parse each playlist out, match the name based on the
            // permalink, and return the id of the matching playlist.
            // a method already exists for downloading xml, so use that and refactor this to not have
            // the client id embedded in the url
            string playlistsJson = RetrieveJson(userApiUrl, clientId);

            JArray playlists = JArray.Parse(playlistsJson);
            IList<JToken> results = playlists.Children().ToList();
            IList<PlaylistItem> playlistsitems = new List<PlaylistItem>();

            //var playlistItems = JsonConvert.DeserializeObject<JsonPoco.PlaylistRoot>(playlistsJson).PlaylistItems;
            foreach (JToken result in results)
            {
                PlaylistItem playlistsitem = JsonConvert.DeserializeObject<PlaylistItem>(result.ToString());
                playlistsitems.Add(playlistsitem);
            }

            var matchingPlaylistItem = playlistsitems.FirstOrDefault(s => s.permalink == playlistName);

            if (matchingPlaylistItem != null)
            {
                return matchingPlaylistItem.id.ToString();
            }
            else
            {
                IsError = true;
                throw new Exception("Unable to find a matching playlist");
            }
        }

        private string ParseUserIdFromProfileUrl(string url)
        {
            try
            {
                string startingPoint = "soundcloud.com/";
                int startingIndex = url.IndexOf(startingPoint, StringComparison.Ordinal) + startingPoint.Length;
                int endingIndex = url.Substring(startingIndex).Contains("/") ?
                    url.Substring(startingIndex).IndexOf("/", StringComparison.Ordinal) + startingIndex :
                    url.Length;

                return url.Substring(startingIndex, endingIndex - startingIndex);
            }
            catch (Exception e)
            {
                IsError = true;
                throw new Exception("Invalid profile url: " + e.Message);
            }
        }

        internal IList<Track> EnumerateTracksFromUrl(string url, string clientId, bool isRawTracksUrl)
        {
            // get the json associated with the playlist from the soundcloud api
            int limit = isRawTracksUrl? 200 : 0; //200 is the limit set by SoundCloud itself. Remember; limits are only with 'collection' types in JSON 
            IList<Track> tracks = new List<Track>();

            try
            {
                // get the tracks embedded in the playlist
                bool tracksAdded = true;

                string tracksJson = RetrieveJson(url, clientId, limit);
                bool lastStep = false;

                while (tracksAdded && tracksJson != null)
                {                    
                    JObject JOBtracksJson = JObject.Parse(tracksJson);
                 
                    IList<JToken> JTOKENcurrentTracks = isRawTracksUrl ? JOBtracksJson["collection"].Children().ToList() : 
                                                                   JOBtracksJson["tracks"].Children().ToList();
                  
                    IList<Track> currentTracks = new List<Track>();
                    foreach (JToken Jtrack in JTOKENcurrentTracks)
                    {
                        Track currentTrack = JsonConvert.DeserializeObject<Track>(Jtrack.ToString());
                        currentTracks.Add(currentTrack);
                    }

                    if (currentTracks.Any())
                    {
                        foreach (Track track in currentTracks)
                        {
                            tracks.Add(track);
                        }
                        tracksAdded = true;
                    }
                    else
                    {
                        tracksAdded = false;
                    }

                    if (lastStep)
                        break;

                    var linkedPartitioningUrl = JsonConvert.DeserializeObject<NextInfo>(tracksJson).next_href;
                    tracksJson = RetrieveJson(linkedPartitioningUrl, null);
                    if (String.IsNullOrEmpty(tracksJson))
                    {
                        lastStep = true;
                    }            
                }
                
            }
            catch (Exception)
            {
                IsError = true;
                throw new Exception("Errors occurred retrieving the tracks list information. Double check your url.");
            }

            return tracks;
        }

        internal void SynchronizeFromProfile(string username, string clientId, string directoryPath)
        {
            // hit the /username/favorites endpoint for the username in the url, then download all the tracks
            IList<Track> tracks = EnumerateTracksFromUrl("https://api.soundcloud.com/users/" + username + "/favorites", clientId, true);
            Synchronize(tracks, clientId, directoryPath);
        }

        public static bool IsPathWithinLimits(string fullPathAndFilename)
        {
            //In the Windows API the maximum length for a path is MAX_PATH, which is defined as 260 characters.
            //We'll make it 250 because there will be an extention and, in some cases, an HQ tag appended to the filename.  
            const int maxPathLength = 250;
            return fullPathAndFilename.Length <= maxPathLength;
        }

        private void Synchronize(IList<Track> tracks, string clientId, string directoryPath)
        {
            //define all local paths by combining the sanitzed artist (if checked by user) with the santized title
            foreach(var track in tracks)
            {
                string validArtist = track.CoerceValidFileName(track.Artist, true);
                string validArtistFolderName = Track.TrimDotsAndSpacesForFolderName(validArtist);
                string validTitle = track.CoerceValidFileName(track.Title, true);
                string filenameWithArtist = validArtist + " - " + validTitle;

                if (Form1.FoldersPerArtist)
                {
                    if (Form1.IncludeArtistInFilename) //include artist name
                    {
                        while (!IsPathWithinLimits(track.LocalPath = Path.Combine(directoryPath, validArtistFolderName,
                            filenameWithArtist)))
                        {
                            filenameWithArtist = filenameWithArtist.Remove(filenameWithArtist.Length - 2); //shorten to fit into max size of path
                        };                
                    }
                    else
                    {
                        while (!IsPathWithinLimits(track.LocalPath = Path.Combine(directoryPath, validArtistFolderName,
                            validTitle)))
                        {
                            validTitle = validTitle.Remove(validTitle.Length - 2); //shorten to fit into max size of path
                        };
                    }
                }
                else
                {
                    if (Form1.IncludeArtistInFilename) //include artist name
                    {
                        while (!IsPathWithinLimits(track.LocalPath = Path.Combine(directoryPath, filenameWithArtist)))
                        {
                            filenameWithArtist = filenameWithArtist.Remove(filenameWithArtist.Length - 2); //shorten to fit into max size of path
                        };
                    }
                    else
                    {
                        while (!IsPathWithinLimits(track.LocalPath = Path.Combine(directoryPath, validTitle)))
                        {
                            validTitle = validTitle.Remove(validTitle.Length - 2); //shorten to fit into max size of path
                        };
                    }
                }
                if (track.IsHD)
                {
                    track.LocalPath += " (HQ)";
                }
            };    
            // determine which tracks should be deleted or re-added
            DeleteOrAddRemovedTrack(directoryPath, tracks);

            // determine which tracks should be downloaded
            DetermineTracksToDownload(directoryPath, ref tracks);
          
            // download the relevant tracks and continuously update the manifest
            DownloadSongs(tracks, clientId, directoryPath);

            //Create playlist file
            bool[] completed = PlaylistCreator.createSimpleM3U(tracks, directoryPath);

            int songstodownload = tracks.Count(x => x.HasToBeDownloaded == true);
            // validation
            if (songstodownload > 0 && IsActive)
            {
                IsError = true;
                throw new Exception(
                        "Some tracks failed to download. You might need to try a few more times before they can download correctly. " +
                        "The following tracks were not downloaded:" + Environment.NewLine +
                        string.Join(Environment.NewLine, tracks.Where(x => x.HasToBeDownloaded == true).Select(x => "Title: " + x.Title + ", Artist: " + x.Artist)
                    ));
            }
        }


        internal void SynchronizeFromPlaylistApiUrl(string playlistApiUrl, string clientId, string directoryPath)
        {
            IList<Track> tracks = EnumerateTracksFromUrl(playlistApiUrl, clientId, false);
            Synchronize(tracks, clientId, directoryPath);
        }


        internal void SynchronizeFromArtistUrl(string artistUrl, string clientId, string directoryPath)
        {

            IList<Track> tracks = EnumerateTracksFromUrl(artistUrl, clientId, true);
            Synchronize(tracks, clientId, directoryPath);
        }


        private void ResetProgress()
        {
            SongsDownloaded = 0;
            SongsToDownload = 0;
            IsActive = true;
            IsError = false;
        }

        private void UpdateSyncManifest(IList<Track> tracksDownloaded, string directoryPath)
        {
            IList<string> content = new List<string>();

            foreach (Track track in tracksDownloaded)
            {
                content.Add(track.EffectiveDownloadUrl + "," + track.LocalPath);
            }

            try
            {
                lock(_writeManifestLock)
                {
                    string manifestPath = DetermineManifestPath(directoryPath);
                    File.AppendAllLines(manifestPath, content); //if file does not exist, this function will create one                                 
                }
            }
            catch (Exception)
            {
                IsError = true;
                throw new Exception("Unable to update manifest");
            }
        }

        private void UpdateSyncManifest(Track trackDownloaded, string directoryPath)
        {
            string track = null;
            track = trackDownloaded.EffectiveDownloadUrl + "," + trackDownloaded.LocalPath.Replace(directoryPath, "");
            IList<string> content = new List<string>();
            content.Add(track);

            bool updateSuccesful = false;
            for (int attempts = 0; attempts < 5; attempts++)
            {
                try
                {
                    lock (_writeManifestLock)
                    {
                        string manifestPath = DetermineManifestPath(directoryPath);
                        File.AppendAllLines(manifestPath, content); //if file does not exist, this function will create one
                        updateSuccesful = true;
                        break;
                    }
                }
                catch (Exception)
                {                  
                }
                System.Threading.Thread.Sleep(50); // Pause 50ms before new attempt
            }
            if(!updateSuccesful)
            {
                IsError = true;
                throw new Exception("Unable to update manifest");
            }
        }

        private void DownloadSongs(IList<Track> Alltracks, string apiKey, string directoryPath)
        {
            object trackLock = new object();
            SongsToDownload =  Alltracks.Count(x => x.HasToBeDownloaded == true);
            Parallel.ForEach(Alltracks.Where(x => x.HasToBeDownloaded == true), 
                new ParallelOptions() {MaxDegreeOfParallelism = Settings.Default.ConcurrentDownloads},
                track =>
            {
                try
                {
                    if (DownloadTrack(track, apiKey))
                    {
                        lock (trackLock)
                        {
                            track.HasToBeDownloaded = false; ;
                            UpdateSyncManifest(track, directoryPath);
                        }
                    }

                }
                catch (Exception e)
                {
                    IsError = true;
                    ExceptionHandler.handleException(e);
                }     
            });
        }

        private bool DownloadTrack(Track song, string apiKey)
        {
            var downloaded = false;
            if (!IsActive) return false;

            using (WebClient client = new WebClient())
            {
                if (song?.LocalPath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(song.LocalPath));

                    if (song.IsHD)
                    {
                        string extension = null;
                        try
                        {
                            WebRequest request = WebRequest.Create(song.EffectiveDownloadUrl +
                                                                   $"?client_id={apiKey}");

                            request.Method = "HEAD";
                            using (WebResponse response = request.GetResponse())
                            {
                                extension = "." + response.Headers["x-amz-meta-file-type"];
                            }
                        }
                        catch (Exception e)
                        {
                            ExceptionHandler.handleException(e);

                            //the download link might have been invalid, so we get the stream download instead
                            if (song.stream_url == null) //all hope is lost when there is no stream url, return to safety
                                return false;

                            var request = WebRequest.Create(song.stream_url + $"?client_id={apiKey}");

                            request.Method = "HEAD";
                            using (var response = request.GetResponse())
                            {
                                extension = "." + response.Headers["x-amz-meta-file-type"];
                            }
                        }
                        var allowedFormats = new List<string>();
                        allowedFormats.AddRange(new string[] { ".wav", ".aiff", ".aif", ".m4a", ".aac"});
                        if(Form1.excludeAAC)
                        {
                            allowedFormats.Remove(".aac");
                        }
                        if(Form1.excludeM4A)
                        {
                            allowedFormats.Remove(".m4a");
                        }
                        if (Form1.ConvertToMp3 && Form1.Highqualitysong && (allowedFormats.Contains(extension)))
                        {
                            //get the wav song as byte data, as we won't store it just yet
                            byte[] soundbytes = client.DownloadData(song.EffectiveDownloadUrl +
                                                                    $"?client_id={apiKey}");
                            //convert to mp3 & then write bytes to file
                            bool succesfulConvert = audioConverter.ConvertAllTheThings(soundbytes, ref song, extension);
                            soundbytes = null;
                            if (!succesfulConvert) //something has gone wrong, download the stream url instead of download url 
                            {
                                song.LocalPath += ".mp3";
                                client.DownloadFile(song.stream_url + $"?client_id={apiKey}", song.LocalPath);         
                            }
                        }
                        else if(extension == ".mp3") //get the high res mp3 without converting
                        {
                            song.LocalPath += extension;
                            client.DownloadFile(song.EffectiveDownloadUrl + $"?client_id={apiKey}", song.LocalPath);
                        }
                        else //get the low res mp3 if all above not possible
                        {
                            song.LocalPath += extension;
                            client.DownloadFile(song.stream_url + $"?client_id={apiKey}", song.LocalPath);
                        };
                    }
                    else
                    {
                        song.LocalPath += ".mp3";
                        client.DownloadFile(song.stream_url + $"?client_id={apiKey}", song.LocalPath);
                    }

                    //tag the song
                    try
                    {
                        MetadataTagging.TagIt(ref song);
                    }
                    catch(Exception e)
                    {
                        Debug.WriteLine("Can't tag song:" + song.LocalPath);
                    }
                }


                lock (_songsDownloadedLock)
                {
                    SongsDownloaded++ ;
                    downloaded = true;
                }
            }
            return downloaded;
        }

        private void DeleteOrAddRemovedTrack(string directoryPath, IList<Track> allTracks)
        {
            string manifestPath = DetermineManifestPath(directoryPath);
            try
            {
                if (File.Exists(manifestPath))
                {
                    string[] songsDownloaded = File.ReadAllLines(manifestPath);
                    IList<string> newManifest = new List<string>();

                    foreach (string songDownloaded in songsDownloaded)
                    {
                        string localTrackpath = ParseTrackPath(songDownloaded, 1);
                        string localPathDownloadedSongRelative = directoryPath + localTrackpath.Replace(directoryPath, "");
                        string songId = new String(ParseTrackPath(songDownloaded, 0).ToCharArray().Where(c => Char.IsDigit(c)).ToArray());
                        string neutralPath = Path.ChangeExtension(localPathDownloadedSongRelative, null);
                        Track soundCloudTrack = null;
                        soundCloudTrack = allTracks.FirstOrDefault(song => song.stream_url.Contains("/" + songId + "/"));
                     
                        bool trackArtistOrNameChanged = false;
                        //WARNING      If we want to look if allTracks contains the downloaded file we need to trim the extention
                        //              because allTracks doesn't store the extention of the path                            
                        trackArtistOrNameChanged = !allTracks.Any(song => song.LocalPath.Contains(neutralPath));
                            
                        //file does not exist anymore, it will be redownloaded by not adding it to the newManifest
                        if (!File.Exists(localPathDownloadedSongRelative))
                        {
                            continue;
                        };
                        //song is changed on SoundCloud (only checks artist and filename), redownload and remove old one.
                        if (trackArtistOrNameChanged && soundCloudTrack != null)
                        {
                            bool localIsHd = ParseTrackPath(songDownloaded, 0).EndsWith("download");
                            if (soundCloudTrack.IsHD == true || (soundCloudTrack.IsHD == false && localIsHd == false))
                            // do not download Low Quality if HQ is already downloaded, even if the track is changed!
                            {
                                if (File.Exists(localPathDownloadedSongRelative))
                                {
                                    File.Delete(localPathDownloadedSongRelative);
                                    DeleteEmptyDirectory(localPathDownloadedSongRelative);
                                }
                                continue;
                            }
                        }
                        //file exists locally but not externally and can be removed
                        if (Form1.SyncMethod == 2 && soundCloudTrack == null)
                        {
                            File.Delete(localPathDownloadedSongRelative);
                            DeleteEmptyDirectory(localPathDownloadedSongRelative);
                        }
                        else
                        {
                            newManifest.Add(songDownloaded);
                        }                  
                    }                 
                    // the manifest is updated again later, but might as well update it here
                    // to save the deletions in event of crash or abort
                    File.WriteAllLines(manifestPath, newManifest);        
                }
            }
            catch (Exception e)
            {
                IsError = true;
                throw new Exception("Unable to read manifest to determine tracks to delete; exception: " + e);
            }      
        }

        public bool DeleteEmptyDirectory(string filenameWithPath)
        {
            if (!Form1.FoldersPerArtist)
                return false;
            string path = Path.GetDirectoryName(filenameWithPath);
            if (path != null && !Directory.EnumerateFileSystemEntries(path).Any()) //folder = empty
            {
                try
                {
                    Directory.Delete(path, false); //recursive not true because should be already empty
                    return true;

                }
                catch(Exception)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private void DetermineTracksToDownload(string directoryPath, ref IList<Track> allSongs)
        {             
            string manifestPath = DetermineManifestPath(directoryPath);
            IList<string> streamUrls = new List<string>();
            IList<string> songsDownloaded = new List<string>();
            if (File.Exists(manifestPath))
            {
                songsDownloaded = File.ReadAllLines(manifestPath);
                foreach (string track in File.ReadAllLines(manifestPath))
                {
                    streamUrls.Add(ParseTrackPath(track,0));
                }               
            }
            foreach (Track track in allSongs)
            {
                if (!streamUrls.Contains(track.EffectiveDownloadUrl))
                    track.HasToBeDownloaded = true;    
                else if(songsDownloaded.Count > 0)
                {   // we need to add the extention to the local path for further use
                    // the only way we can know what the extention was when previously downloaded 
                    // is by checking the file directly, or by checking the manifest file, 
                    // we will do the latter
                    track.LocalPath += (PlaylistCreator.getExtension(songsDownloaded, track.LocalPath));
                }
            }
        }

        private string RetrieveJson(string url, string clientId = null, int? limit = null, int? offset = null)
        {        
            string json = null;
            if (limit == 0)
                limit = null;
         
            if (String.IsNullOrEmpty(url))
                return null;
            try
            {
                using (WebClient client = new WebClient()) 
                {
                    client.Encoding = System.Text.Encoding.UTF8;
                    if (!url.Contains("client_id="))
                    {
                        url += (url.Contains("?") ? "&" : "?") + "client_id=" + clientId;
                    }
                    if (limit != null)
                    {
                        url += "&limit=" + limit;
                    }
                    if (offset != null)
                    {
                        url += "&offset=" + offset;
                    }
                    
                    if(limit != null)
                        url += "&linked_partitioning=1"; //will add next_href to the response

                    json = client.DownloadString(url);
                }
             
            }
            catch (Exception e)
            {
                IsError = true;
                ExceptionHandler.handleException(e);
            }

            return json;
        }

        public string DetermineManifestPath(string directoryPath)
        {
            return Path.Combine(directoryPath, Form1.ManifestName);
        }

        protected string ParseTrackPath(string csv, int position)
        {           
            if(csv != null && csv.IndexOf(',') >= 0)
            {
                //only make 1 split, as a comma (,) can be found in a song name!
                return csv.Split(new[] { ',' }, 2)[position]; //position 0 is streampath, position 1 is local path
            }
            else
            {
                return csv;
            }
        }
    }  
}
